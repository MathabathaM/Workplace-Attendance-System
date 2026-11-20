using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Workplace.Api.Contracts;
using Workplace.Api.Controllers;
using Workplace.Data;
using Workplace.Data.Auth;
using Workplace.Data.Entities;

namespace Workplace.Tests;

public sealed class AttendanceControllerTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private AppDbContext _db = null!;
    private readonly Guid _managerId = Guid.NewGuid();
    private readonly Guid _otherManagerId = Guid.NewGuid();
    private readonly Guid _directReportId = Guid.NewGuid();
    private readonly Guid _otherStaffId = Guid.NewGuid();
    private readonly Guid _attendanceId = Guid.NewGuid();
    private static readonly DateOnly WorkDate = new(2026, 11, 13);

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        _db = new AppDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        var site = new Site { Id = Guid.NewGuid(), Name = "Test Site", Country = "ZA" };
        var department = new Department { Id = Guid.NewGuid(), Name = "Operations" };
        _db.Sites.Add(site);
        _db.Departments.Add(department);
        await _db.SaveChangesAsync();

        _db.Employees.AddRange(
            Employee(_managerId, "MGR-1", "Mia", "Manager", site.Id, department.Id),
            Employee(_otherManagerId, "MGR-2", "Taylor", "Lead", site.Id, department.Id),
            Employee(_directReportId, "EMP-1", "Alex", "Report", site.Id, department.Id, _managerId),
            Employee(_otherStaffId, "EMP-2", "Sam", "Other", site.Id, department.Id, _otherManagerId));
        await _db.SaveChangesAsync();

        _db.AttendanceRecords.AddRange(
            new AttendanceRecord
            {
                Id = _attendanceId,
                EmployeeId = _directReportId,
                ShiftDate = WorkDate,
                ScheduledStart = new TimeOnly(8, 0),
                ClockIn = new TimeOnly(8, 15),
                Status = AttendanceStatuses.Late
            },
            new AttendanceRecord
            {
                Id = Guid.NewGuid(),
                EmployeeId = _otherStaffId,
                ShiftDate = WorkDate,
                ScheduledStart = new TimeOnly(9, 0),
                Status = AttendanceStatuses.Absent
            });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task List_limits_line_manager_to_direct_reports()
    {
        var controller = new AttendanceController(_db,
            new FakeCurrentUser(_managerId, [RoleNames.LineManager]));

        var result = await controller.List(
            new AttendanceListQuery { Date = WorkDate, PageSize = 25 }, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Single(result.Value.Items);
        Assert.Equal("EMP-1", result.Value.Items[0].EmployeeNumber);
        Assert.Equal(1, result.Value.Summary.Total);
    }

    [Fact]
    public async Task Update_saves_audit_reason_with_attendance_change()
    {
        var actorId = Guid.NewGuid();
        var controller = new AttendanceController(_db,
            new FakeCurrentUser(_managerId, [RoleNames.LineManager], actorId));

        var result = await controller.Update(_attendanceId, new UpdateAttendanceRequest
        {
            ScheduledStart = "08:00",
            ClockIn = "08:02",
            ClockOut = "16:30",
            Status = AttendanceStatuses.OnTime,
            Reason = "Supervisor verified the original attendance scan."
        }, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(AttendanceStatuses.OnTime, result.Value.Status);
        var audit = await _db.AuditLogs.SingleAsync();
        Assert.Equal("attendance.update", audit.Action);
        Assert.Equal("attendance_record", audit.EntityType);
        Assert.Equal(actorId, audit.ActorId);
        Assert.Equal("Supervisor verified the original attendance scan.", audit.Reason);
        var savedRecord = await _db.AttendanceRecords.SingleAsync(record => record.Id == _attendanceId);
        Assert.Equal(new TimeOnly(8, 2), savedRecord.ClockIn);
        Assert.Equal(new TimeOnly(16, 30), savedRecord.ClockOut);
    }

    [Fact]
    public async Task Update_rejects_clock_out_without_clock_in()
    {
        var controller = new AttendanceController(_db,
            new FakeCurrentUser(_managerId, [RoleNames.LineManager]));

        var result = await controller.Update(_attendanceId, new UpdateAttendanceRequest
        {
            ScheduledStart = "08:00",
            ClockOut = "16:30",
            Status = AttendanceStatuses.Incomplete,
            Reason = "Clock-out needs a matching clock-in."
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await _db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Update_hides_records_outside_the_line_managers_team()
    {
        var otherRecordId = await _db.AttendanceRecords
            .Where(record => record.EmployeeId == _otherStaffId)
            .Select(record => record.Id)
            .SingleAsync();
        var controller = new AttendanceController(_db,
            new FakeCurrentUser(_managerId, [RoleNames.LineManager]));

        var result = await controller.Update(otherRecordId, new UpdateAttendanceRequest
        {
            ScheduledStart = "09:00",
            ClockIn = "09:00",
            Status = AttendanceStatuses.OnTime,
            Reason = "Manager cannot change another team's attendance."
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(await _db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Report_returns_daily_trends_and_employee_totals_for_the_selected_range()
    {
        var controller = new AttendanceController(_db,
            new FakeCurrentUser(_managerId, [RoleNames.HrOfficer]));

        var result = await controller.Report(new AttendanceReportQuery
        {
            FromDate = WorkDate,
            ToDate = WorkDate.AddDays(2)
        }, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.Summary.Total);
        Assert.Equal(1, result.Value.Summary.Late);
        Assert.Equal(1, result.Value.Summary.Absent);
        Assert.Equal(3, result.Value.DailyTrend.Count);
        Assert.Equal(0, result.Value.DailyTrend[1].Summary.Total);
        Assert.Equal(2, result.Value.Employees.Count);
        Assert.Contains(result.Value.Employees, employee =>
            employee.EmployeeNumber == "EMP-1" && employee.Late == 1);
    }

    [Fact]
    public async Task Report_limits_line_manager_to_direct_reports()
    {
        var controller = new AttendanceController(_db,
            new FakeCurrentUser(_managerId, [RoleNames.LineManager]));

        var result = await controller.Report(new AttendanceReportQuery
        {
            FromDate = WorkDate,
            ToDate = WorkDate
        }, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(1, result.Value.Summary.Total);
        var employee = Assert.Single(result.Value.Employees);
        Assert.Equal("EMP-1", employee.EmployeeNumber);
    }

    [Fact]
    public async Task Report_rejects_ranges_longer_than_366_days()
    {
        var controller = new AttendanceController(_db,
            new FakeCurrentUser(_managerId, [RoleNames.HrOfficer]));

        var result = await controller.Report(new AttendanceReportQuery
        {
            FromDate = WorkDate,
            ToDate = WorkDate.AddDays(366)
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static Employee Employee(
        Guid id, string number, string firstName, string lastName, Guid siteId, Guid departmentId,
        Guid? managerId = null) =>
        new()
        {
            Id = id,
            EmployeeNumber = number,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{number.ToLowerInvariant()}@example.com",
            JobTitle = "Test",
            SiteId = siteId,
            DepartmentId = departmentId,
            ManagerId = managerId
        };

    private sealed class FakeCurrentUser(Guid? employeeId, IReadOnlyCollection<string> roles, Guid? userId = null)
        : ICurrentUser
    {
        public Guid? UserId { get; } = userId ?? Guid.NewGuid();
        public Guid? EmployeeId { get; } = employeeId;
        public IReadOnlyCollection<string> Roles { get; } = roles;
        public IPAddress? IpAddress => null;
    }
}
