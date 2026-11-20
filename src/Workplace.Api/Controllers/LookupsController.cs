using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Contracts;
using Workplace.Api.Services;
using Workplace.Data;
using Workplace.Data.Auth;
using Workplace.Data.Entities;
using Workplace.Data.Queries;

namespace Workplace.Api.Controllers;

/// <summary>Dropdown data for the employee form and filters.</summary>
[ApiController]
[Route("api/lookups")]
[Authorize(Policy = Policies.ViewEmployees)]
public sealed class LookupsController(AppDbContext db, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet]
    public async Task<LookupsResponse> Get(CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new LookupItem(s.Id, s.Name)).ToListAsync(ct);
        var departmentQuery = db.Departments.AsNoTracking().Where(d => d.IsActive);
        if (currentUser.IsInRole(RoleNames.LineManager))
        {
            var visibleDepartmentIds = db.Employees.AsNoTracking().VisibleTo(currentUser)
                .Select(employee => employee.DepartmentId);
            departmentQuery = departmentQuery.Where(department => visibleDepartmentIds.Contains(department.Id));
        }

        var departments = await departmentQuery.OrderBy(d => d.Name)
            .Select(d => new LookupItem(d.Id, d.Name)).ToListAsync(ct);
        var shifts = await db.Shifts.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.StartTime)
            .Select(s => new ShiftLookup(s.Id, s.Name, s.Category, s.StartTime, s.EndTime)).ToListAsync(ct);

        return new LookupsResponse(sites, departments, shifts,
            [EmploymentStatuses.Active, EmploymentStatuses.OnLeave, EmploymentStatuses.Terminated]);
    }
}
