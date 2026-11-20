using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Contracts;
using Workplace.Api.Services;
using Workplace.Data;
using Workplace.Data.Auth;
using Workplace.Data.Entities;

namespace Workplace.Api.Controllers;

[ApiController]
[Route("api/attendance")]
[Authorize(Policy = Policies.ViewAttendance)]
public sealed class AttendanceController(AppDbContext db, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Returns attendance records and totals for a date in the site timezone.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AttendancePageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AttendancePageResponse>> List(
        [FromQuery] AttendanceListQuery query, CancellationToken ct)
    {
        var date = query.Date ?? SiteClock.Today;
        var scoped = ApplyVisibility(db.AttendanceRecords.AsNoTracking()
            .Where(record => record.ShiftDate == date));

        var counts = await scoped.GroupBy(record => record.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(ct);

        var filtered = scoped;
        if (query.DepartmentId is Guid departmentId)
            filtered = filtered.Where(record => record.Employee.DepartmentId == departmentId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = $"%{EscapeLike(query.Search.Trim())}%";
            filtered = filtered.Where(record =>
                EF.Functions.Like(record.Employee.FullName, search, "\\") ||
                EF.Functions.Like(record.Employee.EmployeeNumber, search, "\\"));
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
            filtered = filtered.Where(record => record.Status == query.Status);

        var total = await filtered.CountAsync(ct);
        var records = await filtered
            .OrderBy(record => record.Employee.LastName)
            .ThenBy(record => record.Employee.FirstName)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(record => new
            {
                record.Id,
                record.Employee.EmployeeNumber,
                record.Employee.FullName,
                Department = record.Employee.Department.Name,
                record.ShiftDate,
                record.ScheduledStart,
                record.ClockIn,
                record.ClockOut,
                record.Status
            })
            .ToListAsync(ct);

        var items = records.Select(record => new AttendanceRecordResponse(
            record.Id,
            record.EmployeeNumber,
            record.FullName,
            record.Department,
            record.ScheduledStart,
            record.ClockIn,
            record.ClockOut,
            WorkedMinutes(record.ShiftDate, record.ClockIn, record.ClockOut),
            record.Status)).ToArray();

        var summary = new AttendanceSummary(
            counts.Sum(item => item.Count),
            Count(AttendanceStatuses.OnTime),
            Count(AttendanceStatuses.Late),
            Count(AttendanceStatuses.Absent),
            Count(AttendanceStatuses.Incomplete),
            Count(AttendanceStatuses.ApprovedException));

        return new AttendancePageResponse(date, items, total, query.Page, query.PageSize, summary);

        int Count(string status) => counts.FirstOrDefault(item => item.Key == status)?.Count ?? 0;
    }

    /// <summary>Summarizes daily trends and employee attendance for an authorized report.</summary>
    [HttpGet("reports")]
    [Authorize(Policy = Policies.ViewReports)]
    [ProducesResponseType(typeof(AttendanceReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AttendanceReportResponse>> Report(
        [FromQuery] AttendanceReportQuery query, CancellationToken ct)
    {
        var toDate = query.ToDate ?? SiteClock.Today;
        var fromDate = query.FromDate ?? toDate.AddDays(-29);
        if (fromDate > toDate)
            return BadRequest(new ProblemDetails { Title = "The start date must be on or before the end date." });
        if (toDate.DayNumber - fromDate.DayNumber > 365)
            return BadRequest(new ProblemDetails { Title = "Attendance reports are limited to 366 days." });
        if (query.DepartmentId is Guid departmentId &&
            !await db.Departments.AnyAsync(department => department.Id == departmentId, ct))
            return BadRequest(new ProblemDetails { Title = "Select a valid department." });

        var records = ApplyVisibility(db.AttendanceRecords.AsNoTracking()
            .Where(record => record.ShiftDate >= fromDate && record.ShiftDate <= toDate));
        if (query.DepartmentId is Guid selectedDepartmentId)
            records = records.Where(record => record.Employee.DepartmentId == selectedDepartmentId);

        var dailyCounts = await records
            .GroupBy(record => new { record.ShiftDate, record.Status })
            .Select(group => new AttendanceCount(group.Key.ShiftDate, group.Key.Status, group.Count()))
            .ToListAsync(ct);

        var employeeTotals = await records
            .GroupBy(record => new
            {
                record.Employee.EmployeeNumber,
                record.Employee.FullName,
                Department = record.Employee.Department.Name
            })
            .Select(group => new
            {
                group.Key.EmployeeNumber,
                EmployeeName = group.Key.FullName,
                group.Key.Department,
                Total = group.Count(),
                OnTime = group.Count(record => record.Status == AttendanceStatuses.OnTime),
                Late = group.Count(record => record.Status == AttendanceStatuses.Late),
                Absent = group.Count(record => record.Status == AttendanceStatuses.Absent),
                Incomplete = group.Count(record => record.Status == AttendanceStatuses.Incomplete),
                ApprovedException = group.Count(record => record.Status == AttendanceStatuses.ApprovedException)
            })
            .OrderByDescending(employee => employee.Late + employee.Absent + employee.Incomplete)
            .ThenBy(employee => employee.EmployeeName)
            .Select(employee => new EmployeeAttendanceSummary(
                employee.EmployeeNumber,
                employee.EmployeeName,
                employee.Department,
                employee.Total,
                employee.OnTime,
                employee.Late,
                employee.Absent,
                employee.Incomplete,
                employee.ApprovedException))
            .ToListAsync(ct);

        var dailySummaries = dailyCounts.GroupBy(item => item.ShiftDate)
            .ToDictionary(group => group.Key, group => ToSummary(group));
        var dailyTrend = Enumerable.Range(0, toDate.DayNumber - fromDate.DayNumber + 1)
            .Select(offset =>
            {
                var date = fromDate.AddDays(offset);
                return new AttendanceReportDay(
                    date,
                    dailySummaries.TryGetValue(date, out var summary) ? summary : EmptySummary());
            })
            .ToArray();

        var overallSummary = ToSummary(dailyCounts);
        return new AttendanceReportResponse(
            fromDate, toDate, overallSummary, dailyTrend, employeeTotals);
    }

    /// <summary>Updates an attendance record and writes the required reason to the audit log.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.ManageAttendance)]
    [ProducesResponseType(typeof(AttendanceRecordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AttendanceRecordResponse>> Update(
        Guid id, UpdateAttendanceRequest request, CancellationToken ct)
    {
        if (!TimeOnly.TryParseExact(request.ScheduledStart, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var scheduledStart) ||
            !TryParseTime(request.ClockIn, out var clockIn) ||
            !TryParseTime(request.ClockOut, out var clockOut))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid attendance time",
                Detail = "Enter times in 24-hour HH:mm format."
            });
        }

        if (clockOut.HasValue && !clockIn.HasValue)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Clock-in time required",
                Detail = "A clock-out time cannot be recorded without a clock-in time."
            });
        }

        var record = await db.AttendanceRecords.Include(item => item.Employee)
            .ThenInclude(employee => employee.Department)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (record is null || !CanView(record.Employee)) return NotFound();

        var oldScheduledStart = record.ScheduledStart;
        var oldClockIn = record.ClockIn;
        var oldClockOut = record.ClockOut;
        var oldStatus = record.Status;
        record.ScheduledStart = scheduledStart;
        record.ClockIn = clockIn;
        record.ClockOut = clockOut;
        record.Status = request.Status;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        db.AuditLogs.Add(new AuditLog
        {
            ActorId = currentUser.UserId,
            Action = "attendance.update",
            EntityType = "attendance_record",
            EntityId = record.Id.ToString(),
            Changes = JsonSerializer.Serialize(new
            {
                scheduledStart = new { from = Format(oldScheduledStart), to = Format(scheduledStart) },
                clockIn = new { from = Format(oldClockIn), to = Format(clockIn) },
                clockOut = new { from = Format(oldClockOut), to = Format(clockOut) },
                status = new { from = oldStatus, to = request.Status }
            }),
            Reason = request.Reason.Trim(),
            IpAddress = currentUser.IpAddress
        });

        await db.SaveChangesAsync(ct);

        return new AttendanceRecordResponse(
            record.Id,
            record.Employee.EmployeeNumber,
            record.Employee.FullName,
            record.Employee.Department.Name,
            record.ScheduledStart,
            record.ClockIn,
            record.ClockOut,
            WorkedMinutes(record.ShiftDate, record.ClockIn, record.ClockOut),
            record.Status);
    }

    private IQueryable<AttendanceRecord> ApplyVisibility(IQueryable<AttendanceRecord> query)
    {
        if (currentUser.IsInRole(RoleNames.SystemAdministrator) ||
            currentUser.IsInRole(RoleNames.HrOfficer) ||
            currentUser.IsInRole(RoleNames.Auditor))
            return query;

        if (currentUser.IsInRole(RoleNames.LineManager) && currentUser.EmployeeId is Guid managerId)
            return query.Where(record => record.Employee.ManagerId == managerId);

        return query.Where(_ => false);
    }

    private bool CanView(Employee employee) =>
        currentUser.IsInRole(RoleNames.SystemAdministrator) ||
        currentUser.IsInRole(RoleNames.HrOfficer) ||
        (currentUser.IsInRole(RoleNames.LineManager) && employee.ManagerId == currentUser.EmployeeId);

    private static bool TryParseTime(string? value, out TimeOnly? time)
    {
        if (string.IsNullOrEmpty(value))
        {
            time = null;
            return true;
        }

        if (TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            time = parsed;
            return true;
        }

        time = null;
        return false;
    }

    private static string? Format(TimeOnly? time) =>
        time?.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static AttendanceSummary ToSummary(IEnumerable<AttendanceCount> counts)
    {
        var rows = counts.ToArray();
        return new AttendanceSummary(
            rows.Sum(item => item.Count),
            Count(AttendanceStatuses.OnTime),
            Count(AttendanceStatuses.Late),
            Count(AttendanceStatuses.Absent),
            Count(AttendanceStatuses.Incomplete),
            Count(AttendanceStatuses.ApprovedException));

        int Count(string status) => rows.Where(item => item.Status == status).Sum(item => item.Count);
    }

    private static AttendanceSummary EmptySummary() => new(0, 0, 0, 0, 0, 0);

    private sealed record AttendanceCount(DateOnly ShiftDate, string Status, int Count);

    private static int? WorkedMinutes(DateOnly date, TimeOnly? clockIn, TimeOnly? clockOut)
    {
        if (clockIn is null) return null;

        TimeOnly? end = clockOut;
        if (end is null && date == SiteClock.Today)
        {
            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg"));
            end = TimeOnly.FromDateTime(now.DateTime);
        }

        if (end is null) return null;
        var minutes = (int)(end.Value - clockIn.Value).TotalMinutes;
        return Math.Max(0, minutes < 0 ? minutes + 24 * 60 : minutes);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
