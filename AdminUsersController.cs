using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Contracts;
using Workplace.Api.Services;
using Workplace.Data;
using Workplace.Data.Auth;
using Workplace.Data.Entities;

namespace Workplace.Api.Controllers;

/// <summary>Manages application accounts and role assignments.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = RoleNames.SystemAdministrator)]
public sealed class AdminUsersController(
    AppDbContext db,
    UserManager<AppUser> users,
    RoleManager<AppRole> roles,
    ICurrentUser currentUser) : ControllerBase
{
    [HttpGet("roles")]
    public async Task<ActionResult<IReadOnlyList<string>>> ListRoles(CancellationToken ct)
    {
        var result = await roles.Roles.AsNoTracking()
            .Where(role => role.Name != null)
            .OrderBy(role => role.Name)
            .Select(role => role.Name!)
            .ToListAsync(ct);
        return result;
    }

    [HttpGet("employees")]
    public async Task<ActionResult<IReadOnlyList<AdminEmployeeOption>>> ListEmployeeOptions(CancellationToken ct)
    {
        var result = await db.Employees.AsNoTracking()
            .Where(employee => employee.EmploymentStatus != EmploymentStatuses.Terminated)
            .OrderBy(employee => employee.LastName)
            .ThenBy(employee => employee.FirstName)
            .Select(employee => new AdminEmployeeOption(employee.Id, employee.FullName, employee.Email))
            .ToListAsync(ct);
        return result;
    }

    [HttpGet("users")]
    public async Task<ActionResult<IReadOnlyList<AdminUserResponse>>> ListUsers(CancellationToken ct)
    {
        var accountRows = await users.Users.AsNoTracking()
            .OrderBy(user => user.Email)
            .Select(user => new
            {
                user.Id,
                user.Email,
                user.EmployeeId,
                user.IsActive,
                user.LastLoginAt
            })
            .ToListAsync(ct);

        var employeeIds = accountRows.Where(row => row.EmployeeId.HasValue)
            .Select(row => row.EmployeeId!.Value)
            .ToArray();
        var employeeNames = await db.Employees.AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .ToDictionaryAsync(employee => employee.Id, employee => employee.FullName, ct);

        var result = new List<AdminUserResponse>(accountRows.Count);
        foreach (var account in accountRows)
        {
            var accountRoles = (await users.GetRolesAsync(new AppUser { Id = account.Id }))
                .OrderBy(role => role, StringComparer.Ordinal)
                .ToArray();
            result.Add(new AdminUserResponse(
                account.Id,
                account.Email ?? "",
                account.EmployeeId,
                account.EmployeeId is Guid employeeId && employeeNames.TryGetValue(employeeId, out var employeeName)
                    ? employeeName
                    : null,
                accountRoles,
                account.IsActive,
                account.LastLoginAt));
        }

        return result;
    }

    [HttpGet("users/{id:guid}")]
    public async Task<ActionResult<AdminUserResponse>> GetUser(Guid id, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        return await ToResponseAsync(user, ct);
    }

    [HttpPost("users")]
    public async Task<ActionResult<AdminUserResponse>> CreateUser(
        CreateAdminUserRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
            return Conflict(new ProblemDetails { Title = "A login account already uses that email address." });

        if (await ValidateRolesAsync(request.Roles) is { } roleError)
            return roleError;

        Employee? employee = null;
        if (request.EmployeeId is Guid employeeId)
        {
            employee = await db.Employees.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == employeeId, ct);
            if (employee is null)
                return BadRequest(new ProblemDetails { Title = "Select a valid employee." });
            if (!string.Equals(employee.Email, email, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new ProblemDetails
                {
                    Title = "The account email must match the linked employee's email address."
                });
            if (await users.Users.AnyAsync(user => user.EmployeeId == employeeId, ct))
                return Conflict(new ProblemDetails { Title = "That employee already has a login account." });
        }
        else if (request.Roles.Any(role =>
                     role is RoleNames.Employee or RoleNames.LineManager))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Employee and Line Manager accounts must be linked to an employee record."
            });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            EmployeeId = employee?.Id,
            IsActive = true
        };
        var created = await users.CreateAsync(user, request.TemporaryPassword);
        if (!created.Succeeded)
            return IdentityProblem(created);

        var roleResult = await users.AddToRolesAsync(user, request.Roles);
        if (!roleResult.Succeeded)
            return IdentityProblem(roleResult);

        var claimResult = await users.AddClaimAsync(
            user, new Claim(AppClaimsPrincipalFactory.MustChangePasswordClaim, "true"));
        if (!claimResult.Succeeded)
            return IdentityProblem(claimResult);

        AddAuditLog(
            "user.create",
            user.Id,
            new { email, employeeId = user.EmployeeId, roles = request.Roles, isActive = true });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, await ToResponseAsync(user, ct));
    }

    [HttpPut("users/{id:guid}")]
    public async Task<ActionResult<AdminUserResponse>> UpdateUser(
        Guid id, UpdateAdminUserRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        if (await ValidateRolesAsync(request.Roles) is { } roleError)
            return roleError;

        if (id == currentUser.UserId && !request.IsActive)
            return Conflict(new ProblemDetails { Title = "You cannot deactivate your own account." });

        var currentRoles = await users.GetRolesAsync(user);
        var wasActiveAdministrator = user.IsActive && currentRoles.Contains(RoleNames.SystemAdministrator);
        var willBeActiveAdministrator = request.IsActive &&
                                        request.Roles.Contains(RoleNames.SystemAdministrator);
        if (wasActiveAdministrator && !willBeActiveAdministrator &&
            !await HasAnotherActiveAdministratorAsync(user.Id))
        {
            return Conflict(new ProblemDetails
            {
                Title = "At least one active System Administrator account must remain."
            });
        }

        var rolesToRemove = currentRoles.Except(request.Roles, StringComparer.Ordinal).ToArray();
        var rolesToAdd = request.Roles.Except(currentRoles, StringComparer.Ordinal).ToArray();
        var wasActive = user.IsActive;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (rolesToRemove.Length > 0)
        {
            var removed = await users.RemoveFromRolesAsync(user, rolesToRemove);
            if (!removed.Succeeded) return IdentityProblem(removed);
        }
        if (rolesToAdd.Length > 0)
        {
            var added = await users.AddToRolesAsync(user, rolesToAdd);
            if (!added.Succeeded) return IdentityProblem(added);
        }

        user.IsActive = request.IsActive;
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded) return IdentityProblem(updated);
        if (!request.IsActive || rolesToRemove.Length > 0 || rolesToAdd.Length > 0)
        {
            var stampUpdated = await users.UpdateSecurityStampAsync(user);
            if (!stampUpdated.Succeeded) return IdentityProblem(stampUpdated);
        }

        AddAuditLog("user.update", user.Id, new
        {
            roles = new { from = currentRoles, to = request.Roles },
            isActive = new { from = wasActive, to = user.IsActive }
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ToResponseAsync(user, ct);
    }

    [HttpPost("users/{id:guid}/temporary-password")]
    public async Task<IActionResult> SetTemporaryPassword(
        Guid id, SetAdminUserPasswordRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        if (!user.IsActive)
            return Conflict(new ProblemDetails { Title = "Activate the account before resetting its password." });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var passwordResult = await users.ResetPasswordAsync(user, token, request.TemporaryPassword);
        if (!passwordResult.Succeeded) return IdentityProblem(passwordResult);

        var existingClaims = (await users.GetClaimsAsync(user))
            .Where(claim => claim.Type == AppClaimsPrincipalFactory.MustChangePasswordClaim)
            .ToArray();
        if (existingClaims.Length > 0)
        {
            var removed = await users.RemoveClaimsAsync(user, existingClaims);
            if (!removed.Succeeded) return IdentityProblem(removed);
        }
        var added = await users.AddClaimAsync(
            user, new Claim(AppClaimsPrincipalFactory.MustChangePasswordClaim, "true"));
        if (!added.Succeeded) return IdentityProblem(added);

        AddAuditLog("user.password_reset", user.Id, new { temporaryPasswordSet = true });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    private async Task<AdminUserResponse> ToResponseAsync(AppUser user, CancellationToken ct)
    {
        var accountRoles = (await users.GetRolesAsync(user))
            .OrderBy(role => role, StringComparer.Ordinal)
            .ToArray();
        var employeeName = user.EmployeeId is Guid employeeId
            ? await db.Employees.AsNoTracking()
                .Where(employee => employee.Id == employeeId)
                .Select(employee => employee.FullName)
                .FirstOrDefaultAsync(ct)
            : null;
        return new AdminUserResponse(
            user.Id, user.Email ?? "", user.EmployeeId, employeeName,
            accountRoles, user.IsActive, user.LastLoginAt);
    }

    private async Task<ActionResult?> ValidateRolesAsync(IReadOnlyList<string> requestedRoles)
    {
        var distinctRoles = requestedRoles.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctRoles.Length != requestedRoles.Count)
            return BadRequest(new ProblemDetails { Title = "Each role can only be assigned once." });

        foreach (var role in distinctRoles)
        {
            if (!await roles.RoleExistsAsync(role))
                return BadRequest(new ProblemDetails { Title = $"Unknown role: {role}." });
        }

        return null;
    }

    private async Task<bool> HasAnotherActiveAdministratorAsync(Guid excludedUserId)
    {
        var administrators = await users.GetUsersInRoleAsync(RoleNames.SystemAdministrator);
        return administrators.Any(user => user.Id != excludedUserId && user.IsActive);
    }

    private ActionResult IdentityProblem(IdentityResult result)
    {
        foreach (var error in result.Errors)
            ModelState.AddModelError("account", error.Description);
        return ValidationProblem(ModelState);
    }

    private void AddAuditLog(string action, Guid userId, object changes) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = currentUser.UserId,
            Action = action,
            EntityType = "user",
            EntityId = userId.ToString(),
            Changes = JsonSerializer.Serialize(changes),
            IpAddress = currentUser.IpAddress
        });
}
