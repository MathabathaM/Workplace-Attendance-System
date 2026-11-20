using System.ComponentModel.DataAnnotations;

namespace Workplace.Api.Contracts;

public sealed class LoginRequest
{
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required] public string Password { get; set; } = "";
    public bool RememberMe { get; set; }
}

public sealed class ChangePasswordRequest
{
    [Required] public string CurrentPassword { get; set; } = "";
    [Required] public string NewPassword { get; set; } = "";
}

public sealed record MeResponse(
    Guid Id, string Email, Guid? EmployeeId, string? FullName, IReadOnlyList<string> Roles,
    bool MustChangePassword);
