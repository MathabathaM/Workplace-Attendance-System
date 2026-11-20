using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Contracts;
using Workplace.Api.Services;
using Workplace.Data;
using Workplace.Data.Entities;

namespace Workplace.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    SignInManager<AppUser> signInManager,
    UserManager<AppUser> userManager,
    AppDbContext db) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<MeResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var user = await userManager.FindByEmailAsync(request.Email);

        // Same answer for "no such user", "inactive" and "wrong password" so the
        // endpoint doesn't reveal which accounts exist.
        if (user is null || !user.IsActive)
            return Unauthorized(new ProblemDetails { Title = "Invalid email or password." });

        var result = await signInManager.PasswordSignInAsync(
            user, request.Password, request.RememberMe, lockoutOnFailure: true);

        if (result.IsLockedOut)
            return StatusCode(StatusCodes.Status423Locked,
                new ProblemDetails { Title = "This account is temporarily locked. Try again later." });

        if (!result.Succeeded)
            return Unauthorized(new ProblemDetails { Title = "Invalid email or password." });

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await userManager.UpdateAsync(user);

        return await BuildMeAsync(user, ct);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<MeResponse>> Me(CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        return await BuildMeAsync(user, ct);
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError("newPassword", error.Description);
            return ValidationProblem(ModelState);
        }

        var claims = await userManager.GetClaimsAsync(user);
        var mustChangePassword = claims
            .Where(claim => claim.Type == AppClaimsPrincipalFactory.MustChangePasswordClaim)
            .ToList();
        if (mustChangePassword.Count > 0)
        {
            var removed = await userManager.RemoveClaimsAsync(user, mustChangePassword);
            if (!removed.Succeeded)
                throw new InvalidOperationException(
                    $"Could not clear the temporary-password requirement: {string.Join("; ", removed.Errors.Select(error => error.Description))}");
        }

        await signInManager.RefreshSignInAsync(user);   // keep this session alive with the new stamp
        return NoContent();
    }

    private async Task<MeResponse> BuildMeAsync(AppUser user, CancellationToken ct)
    {
        var roles = (await userManager.GetRolesAsync(user)).OrderBy(r => r).ToList();
        var fullName = user.EmployeeId is Guid employeeId
            ? await db.Employees.AsNoTracking().Where(e => e.Id == employeeId)
                .Select(e => e.FullName).FirstOrDefaultAsync(ct)
            : null;
        var claims = await userManager.GetClaimsAsync(user);
        var mustChangePassword = claims.Any(claim =>
            claim.Type == AppClaimsPrincipalFactory.MustChangePasswordClaim && claim.Value == "true");
        return new MeResponse(user.Id, user.Email ?? "", user.EmployeeId, fullName, roles, mustChangePassword);
    }
}
