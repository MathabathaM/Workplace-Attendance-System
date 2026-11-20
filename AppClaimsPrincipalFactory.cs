using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Workplace.Data.Entities;

namespace Workplace.Api.Services;

/// <summary>Adds the employee_id claim to the sign-in cookie (used for Line Manager scoping).</summary>
public sealed class AppClaimsPrincipalFactory(
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, AppRole>(userManager, roleManager, options)
{
    public const string EmployeeIdClaim = "employee_id";
    public const string MustChangePasswordClaim = "must_change_password";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.EmployeeId is Guid employeeId)
            identity.AddClaim(new Claim(EmployeeIdClaim, employeeId.ToString()));
        return identity;
    }
}
