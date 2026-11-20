using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Controllers;
using Workplace.Data;
using Workplace.Data.Auth;
using Workplace.Data.Entities;

namespace Workplace.Tests;

public sealed class LookupsControllerTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _managerId = Guid.NewGuid();
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

        var site = new Site { Id = Guid.NewGuid(), Name = "Test Site" };
        var teamDepartment = new Department { Id = Guid.NewGuid(), Name = "Operations" };
        var otherDepartment = new Department { Id = Guid.NewGuid(), Name = "Finance" };
        var otherManagerId = Guid.NewGuid();
        _db.Sites.Add(site);
        _db.Departments.AddRange(teamDepartment, otherDepartment);
        _db.Employees.AddRange(
            Employee(_managerId, "MGR-1", site.Id, teamDepartment.Id),
            Employee(otherManagerId, "MGR-2", site.Id, otherDepartment.Id),
            Employee(Guid.NewGuid(), "EMP-1", site.Id, teamDepartment.Id, _managerId),
            Employee(Guid.NewGuid(), "EMP-2", site.Id, otherDepartment.Id, otherManagerId));
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Get_limits_line_manager_department_filters_to_direct_reports()
    {
        var controller = new LookupsController(_db,
            new FakeCurrentUser(_managerId, [RoleNames.LineManager]));

        var result = await controller.Get(CancellationToken.None);

        var department = Assert.Single(result.Departments);
        Assert.Equal("Operations", department.Name);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static Employee Employee(
        Guid id, string employeeNumber, Guid siteId, Guid departmentId, Guid? managerId = null) =>
        new()
        {
            Id = id,
            EmployeeNumber = employeeNumber,
            FirstName = employeeNumber,
            LastName = "Test",
            Email = $"{employeeNumber.ToLowerInvariant()}@example.com",
            JobTitle = "Test",
            SiteId = siteId,
            DepartmentId = departmentId,
            ManagerId = managerId
        };

    private sealed class FakeCurrentUser(Guid? employeeId, IReadOnlyCollection<string> roles) : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public Guid? EmployeeId { get; } = employeeId;
        public IReadOnlyCollection<string> Roles { get; } = roles;
        public IPAddress? IpAddress => null;
    }
}
