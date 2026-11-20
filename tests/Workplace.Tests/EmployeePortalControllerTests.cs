using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Contracts;
using Workplace.Api.Controllers;
using Workplace.Api.Services;
using Workplace.Data;
using Workplace.Data.Auth;
using Workplace.Data.Entities;

namespace Workplace.Tests;

public sealed class EmployeePortalControllerTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _employeeId = Guid.NewGuid();
    private readonly Guid _otherEmployeeId = Guid.NewGuid();
    private AppDbContext _db = null!;

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
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Day shift",
            Category = "day",
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 30),
            BreakMinutes = 30,
            GraceMinutes = 5,
            IsActive = true
        };
        _db.Sites.Add(site);
        _db.Departments.Add(department);
        _db.Shifts.Add(shift);
        _db.Employees.AddRange(
            Employee(_employeeId, "EMP-1", "Alex", "Worker", site.Id, department.Id),
            Employee(_otherEmployeeId, "EMP-2", "Sam", "Other", site.Id, department.Id));
        await _db.SaveChangesAsync();

        _db.EmployeeShiftAssignments.Add(new EmployeeShiftAssignment
        {
            EmployeeId = _employeeId,
            ShiftId = shift.Id,
            EffectiveFrom = SiteClock.Today.AddDays(-10)
        });
        _db.AttendanceRecords.AddRange(
            Attendance(_employeeId, SiteClock.Today, new TimeOnly(8, 0), new TimeOnly(8, 0), new TimeOnly(16, 0)),
            Attendance(_employeeId, SiteClock.Today.AddDays(-1), new TimeOnly(8, 0), null, null, AttendanceStatuses.Absent),
            Attendance(_otherEmployeeId, SiteClock.Today, new TimeOnly(8, 0), new TimeOnly(7, 45), new TimeOnly(16, 30)));
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Portal_returns_only_the_signed_in_employees_schedule_and_attendance()
    {
        var controller = new EmployeePortalController(_db, new FakeCurrentUser(_employeeId));

        var result = await controller.Portal(CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal("EMP-1", result.Value.EmployeeNumber);
        Assert.Equal("Alex Worker", result.Value.EmployeeName);
        Assert.Equal("Day shift", result.Value.AssignedShift?.Name);
        Assert.Equal(AttendanceStatuses.OnTime, result.Value.TodayAttendance?.Status);
        Assert.Equal(480, result.Value.WorkedMinutesThisWeek);
        Assert.Equal(2, result.Value.RecentAttendance.Count);
        Assert.DoesNotContain(result.Value.RecentAttendance, record =>
            record.Status == AttendanceStatuses.Late);
    }

    [Fact]
    public async Task Attendance_history_is_paged_and_scoped_to_the_signed_in_employee()
    {
        var controller = new EmployeePortalController(_db, new FakeCurrentUser(_employeeId));

        var firstPage = await controller.Attendance(
            new EmployeeAttendanceQuery { Page = 1, PageSize = 1 }, CancellationToken.None);
        var secondPage = await controller.Attendance(
            new EmployeeAttendanceQuery { Page = 2, PageSize = 1 }, CancellationToken.None);

        Assert.NotNull(firstPage.Value);
        Assert.Equal(2, firstPage.Value.Total);
        Assert.Single(firstPage.Value.Items);
        Assert.Equal(SiteClock.Today, firstPage.Value.Items[0].ShiftDate);
        Assert.NotNull(secondPage.Value);
        Assert.Equal(AttendanceStatuses.Absent, secondPage.Value.Items[0].Status);
    }

    [Fact]
    public async Task Portal_returns_not_found_when_the_account_is_not_linked_to_an_employee()
    {
        var controller = new EmployeePortalController(_db, new FakeCurrentUser(null));

        var result = await controller.Portal(CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static Employee Employee(
        Guid id, string number, string firstName, string lastName, Guid siteId, Guid departmentId) =>
        new()
        {
            Id = id,
            EmployeeNumber = number,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{number.ToLowerInvariant()}@example.com",
            JobTitle = "Test",
            SiteId = siteId,
            DepartmentId = departmentId
        };

    private static AttendanceRecord Attendance(
        Guid employeeId, DateOnly date, TimeOnly scheduledStart,
        TimeOnly? clockIn, TimeOnly? clockOut, string status = AttendanceStatuses.OnTime) =>
        new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            ShiftDate = date,
            ScheduledStart = scheduledStart,
            ClockIn = clockIn,
            ClockOut = clockOut,
            Status = status
        };

    private sealed class FakeCurrentUser(Guid? employeeId) : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public Guid? EmployeeId { get; } = employeeId;
        public IReadOnlyCollection<string> Roles { get; } = [RoleNames.Employee];
        public IPAddress? IpAddress => null;
    }
}
