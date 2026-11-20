using System.ComponentModel.DataAnnotations;

namespace Workplace.Api.Contracts;

public sealed class EmployeeListQuery
{
    public string? Search { get; set; }
    public Guid? DepartmentId { get; set; }

    [RegularExpression("^(active|on_leave|terminated)$")]
    public string? Status { get; set; }

    /// <summary>name | -name | number</summary>
    [RegularExpression("^(name|-name|number)$")]
    public string? Sort { get; set; }

    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 8;
}

public class EmployeeRequest
{
    [Required, StringLength(30)] public string EmployeeNumber { get; set; } = "";
    [Required, StringLength(100)] public string FirstName { get; set; } = "";
    [Required, StringLength(100)] public string LastName { get; set; } = "";
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(150)] public string JobTitle { get; set; } = "";

    [Required] public Guid? SiteId { get; set; }
    [Required] public Guid? DepartmentId { get; set; }
    [Required] public Guid? ShiftId { get; set; }
    public Guid? ManagerId { get; set; }

    [Required, RegularExpression("^(active|on_leave|terminated)$")]
    public string EmploymentStatus { get; set; } = "active";

    public DateOnly? HireDate { get; set; }
}

/// <summary>Fields used to create an employee and their initial login account.</summary>
public sealed class CreateEmployeeRequest : EmployeeRequest
{
    [Required, StringLength(100, MinimumLength = 12)]
    public string InitialPassword { get; set; } = "";
}

/// <summary>Sets or resets an employee's temporary login password.</summary>
public sealed class SetEmployeeAccountRequest
{
    [Required, StringLength(100, MinimumLength = 12)]
    public string TemporaryPassword { get; set; } = "";
}

public sealed record EmployeeListItem(
    Guid Id, string EmployeeNumber, string FullName, string Email, string JobTitle,
    string Department, string Site, string? Shift, string EmploymentStatus);

public sealed record EmployeeDetail(
    Guid Id, string EmployeeNumber, string FirstName, string LastName, string FullName,
    string Email, string JobTitle,
    Guid SiteId, string Site, Guid DepartmentId, string Department,
    Guid? ShiftId, string? Shift, Guid? ManagerId, string? ManagerName,
    string EmploymentStatus, DateOnly? HireDate, DateOnly? TerminationDate);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>Counts exclude terminated employees.</summary>
public sealed record EmployeeSummary(int Total, int Active, int OnLeave);

public sealed record LookupItem(Guid Id, string Name);
public sealed record ShiftLookup(Guid Id, string Name, string Category, TimeOnly StartTime, TimeOnly EndTime);
public sealed record LookupsResponse(
    IReadOnlyList<LookupItem> Sites,
    IReadOnlyList<LookupItem> Departments,
    IReadOnlyList<ShiftLookup> Shifts,
    IReadOnlyList<string> EmploymentStatuses);
