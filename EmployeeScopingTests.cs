using System.Net;
using Workplace.Data.Auth;
using Workplace.Data.Entities;
using Workplace.Data.Queries;

namespace Workplace.Tests;

/// <summary>
/// Row-scoping rules for employee queries. These run against in-memory lists
/// (no database needed) because VisibleTo is plain LINQ.
/// </summary>
public class EmployeeScopingTests
{
    private static readonly Guid LeratoId = Guid.NewGuid();   // the Line Manager
    private static readonly Guid OtherManagerId = Guid.NewGuid();

    private static readonly List<Employee> Staff =
    [
        new() { Id = LeratoId,        EmployeeNumber = "EMP-1042" },
        new() { Id = Guid.NewGuid(),  EmployeeNumber = "EMP-1156", ManagerId = LeratoId },
        new() { Id = Guid.NewGuid(),  EmployeeNumber = "EMP-1234", ManagerId = LeratoId },
        new() { Id = Guid.NewGuid(),  EmployeeNumber = "EMP-1210", ManagerId = OtherManagerId },
        new() { Id = Guid.NewGuid(),  EmployeeNumber = "EMP-1103" }
    ];

    private static List<string> Visible(FakeUser user) =>
        Staff.AsQueryable().VisibleTo(user).Select(e => e.EmployeeNumber).OrderBy(n => n).ToList();

    [Theory]
    [InlineData(RoleNames.SystemAdministrator)]
    [InlineData(RoleNames.HrOfficer)]
    public void Admin_and_HR_see_everyone(string role)
    {
        var user = new FakeUser { RoleList = [role] };
        Assert.Equal(5, Visible(user).Count);
    }

    [Fact]
    public void Line_manager_sees_only_their_direct_reports()
    {
        var user = new FakeUser { RoleList = [RoleNames.LineManager], EmployeeId = LeratoId };
        Assert.Equal(["EMP-1156", "EMP-1234"], Visible(user));
    }

    [Fact]
    public void Line_manager_does_not_see_other_managers_staff_or_themselves()
    {
        var user = new FakeUser { RoleList = [RoleNames.LineManager], EmployeeId = LeratoId };
        var visible = Visible(user);
        Assert.DoesNotContain("EMP-1210", visible);
        Assert.DoesNotContain("EMP-1042", visible);
    }

    [Fact]
    public void Line_manager_with_no_employee_record_sees_nobody()
    {
        var user = new FakeUser { RoleList = [RoleNames.LineManager], EmployeeId = null };
        Assert.Empty(Visible(user));
    }

    [Theory]
    [InlineData(RoleNames.Employee)]
    [InlineData(RoleNames.Auditor)]
    [InlineData(RoleNames.SecurityOfficer)]
    public void Other_roles_see_nobody(string role)
    {
        var user = new FakeUser { RoleList = [role], EmployeeId = LeratoId };
        Assert.Empty(Visible(user));
    }

    [Fact]
    public void A_user_with_both_manager_and_hr_roles_gets_the_wider_view()
    {
        var user = new FakeUser { RoleList = [RoleNames.LineManager, RoleNames.HrOfficer], EmployeeId = LeratoId };
        Assert.Equal(5, Visible(user).Count);
    }

    private sealed class FakeUser : ICurrentUser
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public Guid? EmployeeId { get; init; }
        public List<string> RoleList { get; init; } = [];
        public IReadOnlyCollection<string> Roles => RoleList;
        public IPAddress? IpAddress => null;
    }
}
