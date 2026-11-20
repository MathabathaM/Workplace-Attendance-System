using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Contracts;
using Workplace.Api.Controllers;
using Workplace.Api.Services;
using Workplace.Data;
using Workplace.Data.Auth;
using Workplace.Data.Auditing;
using Workplace.Data.Entities;

namespace Workplace.Tests;

public sealed class ShiftsControllerTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeCurrentUser _currentUser = new();
    private AppDbContext _db = null!;
    private readonly Guid _employeeId = Guid.NewGuid();
    private readonly Guid _siteId = Guid.NewGuid();
    private readonly Guid _departmentId = Guid.NewGuid();
    private readonly Guid _dayShiftId = Guid.NewGuid();
    private readonly Guid _earlyShiftId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new AuditInterceptor(_currentUser))
            .Options;
        _db = new AppDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _db.Sites.Add(new Site { Id = _siteId, Name = "Test Site", Country = "ZA" });
        _db.Departments.Add(new Department { Id = _departmentId, Name = "Operations" });
        _db.Shifts.AddRange(
            Shift(_dayShiftId, "Day shift", new TimeOnly(8, 0), new TimeOnly(16, 30)),
            Shift(_earlyShiftId, "Early shift", new TimeOnly(6, 0), new TimeOnly(14, 30)));
        _db.Employees.Add(new Employee
        {
            Id = _employeeId,
            EmployeeNumber = "EMP-1",
            FirstName = "Alex",
            LastName = "Worker",
            Email = "alex.worker@example.com",
            JobTitle = "Operator",
            SiteId = _siteId,
            DepartmentId = _departmentId,
            EmploymentStatus = EmploymentStatuses.Active
        });
        await _db.SaveChangesAsync();

        _db.EmployeeShiftAssignments.Add(new EmployeeShiftAssignment
        {
            EmployeeId = _employeeId,
            ShiftId = _dayShiftId,
            EffectiveFrom = SiteClock.Today.AddDays(-10)
        });
        await _db.SaveChangesAsync();
        await _db.AuditLogs.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task List_returns_active_and_inactive_shift_pattern_counts()
    {
        var controller = new ShiftsController(_db);

        var result = await controller.List(CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, shift => shift.Id == _dayShiftId && shift.AssignedEmployeeCount == 1);
        Assert.Contains(result.Value, shift => shift.Id == _earlyShiftId && shift.AssignedEmployeeCount == 0);
    }

    [Fact]
    public async Task Assign_closes_previous_assignment_and_audits_both_changes()
    {
        var controller = new ShiftsController(_db);

        var result = await controller.Assign(_employeeId,
            new AssignShiftRequest { ShiftId = _earlyShiftId }, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(_earlyShiftId, result.Value.ShiftId);
        Assert.Equal(SiteClock.Today, result.Value.EffectiveFrom);
        var assignments = await _db.EmployeeShiftAssignments
            .OrderBy(assignment => assignment.EffectiveFrom).ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(SiteClock.Today.AddDays(-1), assignments[0].EffectiveTo);
        Assert.Null(assignments[1].EffectiveTo);
        var auditRows = await _db.AuditLogs.OrderBy(log => log.Id).ToListAsync();
        Assert.Equal(["employee_shift.update", "employee_shift.create"],
            auditRows.Select(log => log.Action));
        Assert.All(auditRows, log => Assert.Equal(_currentUser.UserId, log.ActorId));
    }

    [Fact]
    public async Task Deactivate_rejects_a_shift_that_still_has_assignees()
    {
        var controller = new ShiftsController(_db);

        var result = await controller.Deactivate(_dayShiftId, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.True(await _db.Shifts.Where(shift => shift.Id == _dayShiftId)
            .Select(shift => shift.IsActive).SingleAsync());
    }

    [Fact]
    public async Task Deactivate_succeeds_after_the_employees_are_reassigned()
    {
        var controller = new ShiftsController(_db);
        await controller.Assign(_employeeId,
            new AssignShiftRequest { ShiftId = _earlyShiftId }, CancellationToken.None);

        var result = await controller.Deactivate(_dayShiftId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await _db.Shifts.Where(shift => shift.Id == _dayShiftId)
            .Select(shift => shift.IsActive).SingleAsync());
    }

    [Fact]
    public async Task Create_adds_and_audits_an_overnight_shift_pattern()
    {
        var controller = new ShiftsController(_db);

        var result = await controller.Create(new ShiftRequest
        {
            Name = "Night 22:00",
            Category = "night",
            StartTime = "22:00",
            EndTime = "06:00",
            BreakMinutes = 30,
            GraceMinutes = 5,
            CrossesMidnight = true
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<Workplace.Api.Contracts.ShiftResponse>(created.Value);
        Assert.Equal("Night 22:00", response.Name);
        Assert.True(response.CrossesMidnight);
        var audit = await _db.AuditLogs.SingleAsync();
        Assert.Equal("shift.create", audit.Action);
        Assert.Equal("shift", audit.EntityType);
        Assert.Equal(_currentUser.UserId, audit.ActorId);
    }

    [Fact]
    public async Task Create_rejects_an_overnight_shift_without_the_overnight_flag()
    {
        var controller = new ShiftsController(_db);

        var result = await controller.Create(new ShiftRequest
        {
            Name = "Invalid overnight",
            Category = "night",
            StartTime = "22:00",
            EndTime = "06:00",
            BreakMinutes = 30,
            GraceMinutes = 5,
            CrossesMidnight = false
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await _db.Shifts.AnyAsync(shift => shift.Name == "Invalid overnight"));
        Assert.Empty(await _db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Update_changes_shift_rules_and_writes_an_audit_entry()
    {
        var controller = new ShiftsController(_db);

        var result = await controller.Update(_earlyShiftId, new ShiftRequest
        {
            Name = "Early 06:30",
            Category = "early",
            StartTime = "06:30",
            EndTime = "14:30",
            BreakMinutes = 45,
            GraceMinutes = 10,
            CrossesMidnight = false,
            IsActive = true
        }, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal("Early 06:30", result.Value.Name);
        Assert.Equal(new TimeOnly(6, 30), result.Value.StartTime);
        Assert.Equal(45, result.Value.BreakMinutes);
        var audit = await _db.AuditLogs.SingleAsync();
        Assert.Equal("shift.update", audit.Action);
        Assert.Equal(_currentUser.UserId, audit.ActorId);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static Shift Shift(Guid id, string name, TimeOnly start, TimeOnly end) =>
        new()
        {
            Id = id,
            Name = name,
            Category = "day",
            StartTime = start,
            EndTime = end,
            BreakMinutes = 30,
            GraceMinutes = 5,
            IsActive = true
        };

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public Guid? EmployeeId => null;
        public IReadOnlyCollection<string> Roles => [RoleNames.SystemAdministrator];
        public IPAddress? IpAddress => null;
    }
}
