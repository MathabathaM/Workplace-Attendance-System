using System.Linq.Expressions;
using System.Security.Claims;
using Npgsql;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Contracts;
using Workplace.Api.Services;
using Workplace.Data;
using Workplace.Data.Auth;
using Workplace.Data.Entities;
using Workplace.Data.Queries;

namespace Workplace.Api.Controllers;

[ApiController]
[Route("api/employees")]
public sealed class EmployeesController(
    AppDbContext db,
    ICurrentUser currentUser,
    UserManager<AppUser> userManager) : ControllerBase
{
    // Shared projection: one row of the directory table.
    private static readonly Expression<Func<Employee, EmployeeListItem>> ToListItem = e => new EmployeeListItem(
        e.Id, e.EmployeeNumber, e.FullName, e.Email, e.JobTitle,
        e.Department.Name, e.Site.Name,
        e.ShiftAssignments.Where(a => a.EffectiveTo == null).Select(a => a.Shift.Name).FirstOrDefault(),
        e.EmploymentStatus);

    private static readonly Expression<Func<Employee, EmployeeDetail>> ToDetail = e => new EmployeeDetail(
        e.Id, e.EmployeeNumber, e.FirstName, e.LastName, e.FullName, e.Email, e.JobTitle,
        e.SiteId, e.Site.Name, e.DepartmentId, e.Department.Name,
        e.ShiftAssignments.Where(a => a.EffectiveTo == null).Select(a => (Guid?)a.ShiftId).FirstOrDefault(),
        e.ShiftAssignments.Where(a => a.EffectiveTo == null).Select(a => a.Shift.Name).FirstOrDefault(),
        e.ManagerId, e.Manager != null ? e.Manager.FullName : null,
        e.EmploymentStatus, e.HireDate, e.TerminationDate);

    // ------------------------------------------------------------------ list
    [HttpGet]
    [Authorize(Policy = Policies.ViewEmployees)]
    public async Task<ActionResult<PagedResult<EmployeeListItem>>> List(
        [FromQuery] EmployeeListQuery q, CancellationToken ct)
    {
        var query = db.Employees.AsNoTracking().VisibleTo(currentUser);

        // Terminated staff are hidden unless explicitly requested.
        query = string.IsNullOrEmpty(q.Status)
            ? query.Where(e => e.EmploymentStatus != EmploymentStatuses.Terminated)
            : query.Where(e => e.EmploymentStatus == q.Status);

        if (q.DepartmentId is Guid departmentId)
            query = query.Where(e => e.DepartmentId == departmentId);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = $"%{EscapeLike(q.Search.Trim().ToLowerInvariant())}%";
            query = query.Where(e =>
                EF.Functions.Like(e.FullName.ToLower(), pattern, "\\") ||
                EF.Functions.Like(e.EmployeeNumber.ToLower(), pattern, "\\") ||
                EF.Functions.Like(e.JobTitle.ToLower(), pattern, "\\") ||
                EF.Functions.Like(e.Department.Name.ToLower(), pattern, "\\"));
        }

        query = q.Sort switch
        {
            "-name" => query.OrderByDescending(e => e.LastName).ThenByDescending(e => e.FirstName),
            "number" => query.OrderBy(e => e.EmployeeNumber),
            _ => query.OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
        };

        var total = await query.CountAsync(ct);
        var items = await query.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(ToListItem).ToListAsync(ct);

        return new PagedResult<EmployeeListItem>(items, total, q.Page, q.PageSize);
    }

    // --------------------------------------------------------------- summary
    [HttpGet("summary")]
    [Authorize(Policy = Policies.ViewEmployees)]
    public async Task<EmployeeSummary> Summary(CancellationToken ct)
    {
        var counts = await db.Employees.AsNoTracking().VisibleTo(currentUser)
            .GroupBy(e => e.EmploymentStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var active = counts.FirstOrDefault(c => c.Status == EmploymentStatuses.Active)?.Count ?? 0;
        var onLeave = counts.FirstOrDefault(c => c.Status == EmploymentStatuses.OnLeave)?.Count ?? 0;
        return new EmployeeSummary(active + onLeave, active, onLeave);
    }

    // ------------------------------------------------------------------- one
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.ViewEmployees)]
    public async Task<ActionResult<EmployeeDetail>> Get(Guid id, CancellationToken ct)
    {
        var employee = await db.Employees.AsNoTracking().VisibleTo(currentUser)
            .Where(e => e.Id == id).Select(ToDetail).FirstOrDefaultAsync(ct);

        if (employee is null) return NotFound();
        return employee;
    }

    // ---------------------------------------------------------------- create
    [HttpPost]
    [Authorize(Policy = Policies.ManageEmployees)]
    public async Task<ActionResult<EmployeeDetail>> Create(CreateEmployeeRequest request, CancellationToken ct)
    {
        if (await ValidateReferencesAsync(request, selfId: null, ct) is { } invalid) return invalid;

        if (request.EmploymentStatus == EmploymentStatuses.Terminated)
            return BadRequest(new ProblemDetails { Title = "A login account cannot be created for a terminated employee." });

        if (await userManager.FindByEmailAsync(request.Email.Trim()) is not null)
            return Conflict(new ProblemDetails { Title = "A login account already uses that email address." });

        var employee = new Employee { Id = Guid.NewGuid() };
        Apply(employee, request);
        var user = new AppUser
        {
            UserName = employee.Email,
            Email = employee.Email,
            EmailConfirmed = true,
            EmployeeId = employee.Id,
            IsActive = true
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            db.Employees.Add(employee);
            db.EmployeeShiftAssignments.Add(new EmployeeShiftAssignment
            {
                Employee = employee,
                ShiftId = request.ShiftId!.Value,
                EffectiveFrom = SiteClock.Today
            });
            await db.SaveChangesAsync(ct);

            var createdUser = await userManager.CreateAsync(user, request.InitialPassword);
            if (!createdUser.Succeeded)
            {
                if (createdUser.Errors.Any(error => error.Code is "DuplicateEmail" or "DuplicateUserName"))
                    return Conflict(new ProblemDetails { Title = "A login account already uses that email address." });
                AddIdentityErrors("initialPassword", createdUser);
                return ValidationProblem(ModelState);
            }

            await AddEmployeeRoleAndTemporaryPasswordClaimAsync(user);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (UniqueViolationMessage(ex) is { } message)
        {
            return Conflict(new ProblemDetails { Title = message });
        }

        var created = await db.Employees.AsNoTracking()
            .Where(e => e.Id == employee.Id).Select(ToDetail).FirstAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = employee.Id }, created);
    }

    // ------------------------------------------------------- employee account
    [HttpPost("{id:guid}/account")]
    [Authorize(Policy = Policies.ManageEmployees)]
    public async Task<IActionResult> SetAccount(
        Guid id, SetEmployeeAccountRequest request, CancellationToken ct)
    {
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null) return NotFound();
        if (employee.EmploymentStatus == EmploymentStatuses.Terminated)
            return BadRequest(new ProblemDetails { Title = "A login account cannot be enabled for a terminated employee." });

        var account = await userManager.Users.SingleOrDefaultAsync(user => user.EmployeeId == id, ct);
        var emailAccount = await userManager.FindByEmailAsync(employee.Email);
        if (emailAccount is not null && emailAccount.EmployeeId != id)
            return Conflict(new ProblemDetails { Title = "That email address belongs to another login account." });

        if (account is not null)
        {
            var roles = await userManager.GetRolesAsync(account);
            if (roles.Any(role => role != RoleNames.Employee))
                return Conflict(new ProblemDetails
                {
                    Title = "This employee is linked to a privileged account. Its password cannot be reset from the employee directory."
                });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (account is null)
        {
            account = new AppUser
            {
                UserName = employee.Email,
                Email = employee.Email,
                EmailConfirmed = true,
                EmployeeId = employee.Id,
                IsActive = true
            };
            var created = await userManager.CreateAsync(account, request.TemporaryPassword);
            if (!created.Succeeded)
            {
                if (created.Errors.Any(error => error.Code is "DuplicateEmail" or "DuplicateUserName"))
                    return Conflict(new ProblemDetails { Title = "That email address belongs to another login account." });
                AddIdentityErrors("temporaryPassword", created);
                return ValidationProblem(ModelState);
            }
        }
        else
        {
            if (!string.Equals(account.Email, employee.Email, StringComparison.OrdinalIgnoreCase))
            {
                var emailResult = await userManager.SetEmailAsync(account, employee.Email);
                if (!emailResult.Succeeded)
                {
                    AddIdentityErrors("email", emailResult);
                    return ValidationProblem(ModelState);
                }

                var userNameResult = await userManager.SetUserNameAsync(account, employee.Email);
                if (!userNameResult.Succeeded)
                {
                    AddIdentityErrors("email", userNameResult);
                    return ValidationProblem(ModelState);
                }
            }

            var token = await userManager.GeneratePasswordResetTokenAsync(account);
            var reset = await userManager.ResetPasswordAsync(account, token, request.TemporaryPassword);
            if (!reset.Succeeded)
            {
                AddIdentityErrors("temporaryPassword", reset);
                return ValidationProblem(ModelState);
            }
        }

        await AddEmployeeRoleAndTemporaryPasswordClaimAsync(account);
        await tx.CommitAsync(ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- update
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.ManageEmployees)]
    public async Task<ActionResult<EmployeeDetail>> Update(Guid id, EmployeeRequest request, CancellationToken ct)
    {
        if (await ValidateReferencesAsync(request, selfId: id, ct) is { } invalid) return invalid;

        var employee = await db.Employees
            .Include(e => e.ShiftAssignments.Where(a => a.EffectiveTo == null))
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null) return NotFound();

        var linkedAccount = await userManager.Users.SingleOrDefaultAsync(user => user.EmployeeId == id, ct);
        var accountWithRequestedEmail = await userManager.FindByEmailAsync(request.Email.Trim());
        if (accountWithRequestedEmail is not null && accountWithRequestedEmail.EmployeeId != id)
            return Conflict(new ProblemDetails { Title = "That email address belongs to another login account." });

        var today = SiteClock.Today;
        var wasTerminated = employee.EmploymentStatus == EmploymentStatuses.Terminated;
        Apply(employee, request);
        if (employee.EmploymentStatus != EmploymentStatuses.Terminated)
            employee.TerminationDate = null;
        else if (!wasTerminated)
            employee.TerminationDate = today;

        // Shift change: keep history. Close the current assignment and open a new one,
        // unless the current one only started today, in which case amend it in place.
        var current = employee.ShiftAssignments.FirstOrDefault();
        var newShiftId = request.ShiftId!.Value;
        EmployeeShiftAssignment? replacement = null;

        if (current is null || current.ShiftId != newShiftId)
        {
            if (current is not null && current.EffectiveFrom >= today)
            {
                current.ShiftId = newShiftId;
            }
            else
            {
                if (current is not null) current.EffectiveTo = today.AddDays(-1);
                replacement = new EmployeeShiftAssignment
                {
                    EmployeeId = employee.Id,
                    ShiftId = newShiftId,
                    EffectiveFrom = today
                };
            }
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Two saves in one transaction: the old assignment must be closed BEFORE the
            // new one is inserted (only one open assignment per employee is allowed).
            await db.SaveChangesAsync(ct);
            if (linkedAccount is not null)
            {
                if (!string.Equals(linkedAccount.Email, employee.Email, StringComparison.OrdinalIgnoreCase))
                {
                    var emailResult = await userManager.SetEmailAsync(linkedAccount, employee.Email);
                    if (!emailResult.Succeeded)
                    {
                        AddIdentityErrors("email", emailResult);
                        return ValidationProblem(ModelState);
                    }

                    var userNameResult = await userManager.SetUserNameAsync(linkedAccount, employee.Email);
                    if (!userNameResult.Succeeded)
                    {
                        AddIdentityErrors("email", userNameResult);
                        return ValidationProblem(ModelState);
                    }
                }

                linkedAccount.IsActive = employee.EmploymentStatus != EmploymentStatuses.Terminated;
                var accountUpdate = await userManager.UpdateAsync(linkedAccount);
                if (!accountUpdate.Succeeded)
                {
                    AddIdentityErrors("email", accountUpdate);
                    return ValidationProblem(ModelState);
                }
            }
            if (replacement is not null)
            {
                db.EmployeeShiftAssignments.Add(replacement);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (UniqueViolationMessage(ex) is { } message)
        {
            return Conflict(new ProblemDetails { Title = message });
        }

        var updated = await db.Employees.AsNoTracking()
            .Where(e => e.Id == id).Select(ToDetail).FirstAsync(ct);
        return updated;
    }

    // --------------------------------------------------------------- helpers
    private static void Apply(Employee employee, EmployeeRequest r)
    {
        employee.EmployeeNumber = r.EmployeeNumber.Trim();
        employee.FirstName = r.FirstName.Trim();
        employee.LastName = r.LastName.Trim();
        employee.Email = r.Email.Trim();
        employee.JobTitle = r.JobTitle.Trim();
        employee.SiteId = r.SiteId!.Value;
        employee.DepartmentId = r.DepartmentId!.Value;
        employee.ManagerId = r.ManagerId;
        employee.EmploymentStatus = r.EmploymentStatus;
        employee.HireDate = r.HireDate;
        employee.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task AddEmployeeRoleAndTemporaryPasswordClaimAsync(AppUser user)
    {
        if (!await userManager.IsInRoleAsync(user, RoleNames.Employee))
        {
            var roleResult = await userManager.AddToRoleAsync(user, RoleNames.Employee);
            if (!roleResult.Succeeded)
                throw new InvalidOperationException(
                    $"Could not assign the Employee role: {string.Join("; ", roleResult.Errors.Select(error => error.Description))}");
        }

        var claims = await userManager.GetClaimsAsync(user);
        var previousClaims = claims
            .Where(claim => claim.Type == AppClaimsPrincipalFactory.MustChangePasswordClaim)
            .ToList();
        if (previousClaims.Count > 0)
        {
            var removed = await userManager.RemoveClaimsAsync(user, previousClaims);
            if (!removed.Succeeded)
                throw new InvalidOperationException(
                    $"Could not reset the temporary-password requirement: {string.Join("; ", removed.Errors.Select(error => error.Description))}");
        }

        var added = await userManager.AddClaimAsync(
            user, new Claim(AppClaimsPrincipalFactory.MustChangePasswordClaim, "true"));
        if (!added.Succeeded)
            throw new InvalidOperationException(
                $"Could not require a password change: {string.Join("; ", added.Errors.Select(error => error.Description))}");
    }

    private void AddIdentityErrors(string field, IdentityResult result)
    {
        foreach (var error in result.Errors)
            ModelState.AddModelError(field, error.Description);
    }

    private async Task<ActionResult?> ValidateReferencesAsync(EmployeeRequest r, Guid? selfId, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == r.SiteId, ct))
            ModelState.AddModelError("siteId", "Select a valid site.");
        if (!await db.Departments.AnyAsync(d => d.Id == r.DepartmentId && d.IsActive, ct))
            ModelState.AddModelError("departmentId", "Select a valid department.");
        if (!await db.Shifts.AnyAsync(s => s.Id == r.ShiftId && s.IsActive, ct))
            ModelState.AddModelError("shiftId", "Select a valid shift.");

        if (r.ManagerId is Guid managerId)
        {
            if (managerId == selfId)
                ModelState.AddModelError("managerId", "An employee cannot be their own manager.");
            else if (!await db.Employees.AnyAsync(e => e.Id == managerId, ct))
                ModelState.AddModelError("managerId", "Select a valid manager.");
        }

        return ModelState.IsValid ? null : ValidationProblem(ModelState);
    }

    private static string? UniqueViolationMessage(DbUpdateException ex)
    {
        if (ex.InnerException is not PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation
            } postgres)
            return null;

        var constraint = postgres.ConstraintName ?? "";
        if (constraint.Contains("employee_number", StringComparison.OrdinalIgnoreCase))
            return "That employee number is already in use.";
        if (constraint.Contains("email", StringComparison.OrdinalIgnoreCase))
            return "That email address is already in use.";
        return "A record with these details already exists.";
    }

    private static string EscapeLike(string input) =>
        input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
