using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Contracts;
using Workplace.Api.Services;
using Workplace.Data;
using Workplace.Data.Auth;

namespace Workplace.Api.Controllers;

/// <summary>Provides read-only self-service data to the signed-in employee.</summary>
[ApiController]
[Route("api/me")]
[Authorize(Roles = RoleNames.Employee)]
public sealed class EmployeePortalController(AppDbContext db, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Returns the employee's personal shift and attendance overview.</summary>
    [HttpGet("portal")]
    [ProducesResponseType(typeof(EmployeePortalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EmployeePortalResponse>> Portal(CancellationToken ct)
    {
        if (currentUser.EmployeeId is not Guid employeeId)
            return NotFound();

        var today = SiteClock.Today;
        var employee = await db.Employees.AsNoTracking()
            .Where(item => item.Id == employeeId)
            .Select(item => new
            {
                item.EmployeeNumber,
                EmployeeName = item.FullName,
                Department = item.Department.Name,
                AssignedShift = item.ShiftAssignments
                    .Where(assignment => assignment.EffectiveTo == null && assignment.EffectiveFrom <= today)
                    .Select(assignment => new EmployeePortalShiftResponse(
                        assignment.Shift.Name,
                        assignment.Shift.Category,
                        assignment.Shift.StartTime,
                        assignment.Shift.EndTime,
                        assignment.Shift.BreakMinutes,
                        assignment.Shift.CrossesMidnight,
                        assignment.EffectiveFrom))
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (employee is null)
            return NotFound();

        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weekRecords = await db.AttendanceRecords.AsNoTracking()
            .Where(record => record.EmployeeId == employeeId &&
                record.ShiftDate >= weekStart && record.ShiftDate <= today)
            .Select(record => new AttendanceProjection(
                record.ShiftDate,
                record.ScheduledStart,
                record.ClockIn,
                record.ClockOut,
                record.Status))
            .ToListAsync(ct);
        var workedMinutesThisWeek = weekRecords.Sum(record =>
            WorkedMinutes(record.ShiftDate, record.ClockIn, record.ClockOut, today) ?? 0);

        var todayAttendance = await db.AttendanceRecords.AsNoTracking()
            .Where(record => record.EmployeeId == employeeId && record.ShiftDate == today)
            .Select(record => new AttendanceProjection(
                record.ShiftDate,
                record.ScheduledStart,
                record.ClockIn,
                record.ClockOut,
                record.Status))
            .FirstOrDefaultAsync(ct);

        var recentAttendance = await db.AttendanceRecords.AsNoTracking()
            .Where(record => record.EmployeeId == employeeId)
            .OrderByDescending(record => record.ShiftDate)
            .Take(5)
            .Select(record => new AttendanceProjection(
                record.ShiftDate,
                record.ScheduledStart,
                record.ClockIn,
                record.ClockOut,
                record.Status))
            .ToListAsync(ct);

        return new EmployeePortalResponse(
            employee.EmployeeNumber,
            employee.EmployeeName,
            employee.Department,
            today,
            employee.AssignedShift,
            todayAttendance is null ? null : ToResponse(todayAttendance),
            workedMinutesThisWeek,
            recentAttendance.Select(ToResponse).ToArray());
    }

    /// <summary>Returns only the signed-in employee's paged attendance history.</summary>
    [HttpGet("attendance")]
    [ProducesResponseType(typeof(EmployeeAttendancePageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EmployeeAttendancePageResponse>> Attendance(
        [FromQuery] EmployeeAttendanceQuery query, CancellationToken ct)
    {
        if (currentUser.EmployeeId is not Guid employeeId ||
            !await db.Employees.AnyAsync(item => item.Id == employeeId, ct))
            return NotFound();

        var attendance = db.AttendanceRecords.AsNoTracking()
            .Where(record => record.EmployeeId == employeeId);
        var total = await attendance.CountAsync(ct);
        var records = await attendance
            .OrderByDescending(record => record.ShiftDate)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(record => new AttendanceProjection(
                record.ShiftDate,
                record.ScheduledStart,
                record.ClockIn,
                record.ClockOut,
                record.Status))
            .ToListAsync(ct);

        var items = records.Select(ToResponse).ToArray();
        return new EmployeeAttendancePageResponse(items, total, query.Page, query.PageSize);
    }

    private static EmployeeAttendanceResponse ToResponse(AttendanceProjection record) =>
        new(
            record.ShiftDate,
            record.ScheduledStart,
            record.ClockIn,
            record.ClockOut,
            WorkedMinutes(record.ShiftDate, record.ClockIn, record.ClockOut, SiteClock.Today),
            record.Status);

    private sealed record AttendanceProjection(
        DateOnly ShiftDate,
        TimeOnly ScheduledStart,
        TimeOnly? ClockIn,
        TimeOnly? ClockOut,
        string Status);

    private static int? WorkedMinutes(
        DateOnly date, TimeOnly? clockIn, TimeOnly? clockOut, DateOnly today)
    {
        if (clockIn is null) return null;

        var end = clockOut;
        if (end is null && date == today)
        {
            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg"));
            end = TimeOnly.FromDateTime(now.DateTime);
        }

        if (end is null) return null;
        var minutes = (int)(end.Value - clockIn.Value).TotalMinutes;
        return Math.Max(0, minutes < 0 ? minutes + 24 * 60 : minutes);
    }
}
