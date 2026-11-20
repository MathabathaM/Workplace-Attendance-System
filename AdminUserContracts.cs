using System.ComponentModel.DataAnnotations;

namespace Workplace.Api.Contracts;

/// <summary>Creates a login account and assigns its initial roles.</summary>
public sealed record CreateAdminUserRequest
{
    [Required, EmailAddress, StringLength(254)]
    public required string Email { get; init; }

    [Required, StringLength(100, MinimumLength = 12)]
    public required string TemporaryPassword { get; init; }

    public Guid? EmployeeId { get; init; }

    [Required, MinLength(1)]
    public required IReadOnlyList<string> Roles { get; init; }
}

/// <summary>Changes account roles and whether the account can sign in.</summary>
public sealed record UpdateAdminUserRequest
{
    [Required, MinLength(1)]
    public required IReadOnlyList<string> Roles { get; init; }

    public required bool IsActive { get; init; }
}

/// <summary>Sets a temporary password that must be changed at next sign-in.</summary>
public sealed record SetAdminUserPasswordRequest
{
    [Required, StringLength(100, MinimumLength = 12)]
    public required string TemporaryPassword { get; init; }
}

/// <summary>A login account, its employee link, roles, and active state.</summary>
public sealed record AdminUserResponse(
    Guid Id,
    string Email,
    Guid? EmployeeId,
    string? EmployeeName,
    IReadOnlyList<string> Roles,
    bool IsActive,
    DateTimeOffset? LastLoginAt);

/// <summary>An employee that can be linked to a login account.</summary>
public sealed record AdminEmployeeOption(Guid Id, string FullName, string Email);
