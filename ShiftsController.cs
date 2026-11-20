using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Contracts;
using Workplace.Api.Services;
using Workplace.Data;
using Workplace.Data.Entities;

namespace Workplace.Api.Controllers;

/// <summary>Manages reusable shift patterns and employees' recurring assignments.</summary>
[ApiController]
[Route("api/shifts")]
[Authorize(Policy = Policies.ViewSchedules)]
public sealed class ShiftsController(AppDbContext db) : ControllerBase
{
    /// <summary>Lists all configured shift patterns.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ShiftResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ShiftResponse>>> List(CancellationToken ct)
    {
        var shifts = await db.Shifts.AsNoTracking()
            .OrderBy(shift => shift.StartTime)
            .ThenBy(shift => shift.Name)
            .Select(shift => new ShiftResponse(
                shift.Id,
                shift.Name,
                shift.Category,
                shift.StartTime,
                shift.EndTime,
                shift.BreakMinutes,
                shift.GraceMinutes,
                shift.CrossesMidnight,
                shift.IsActive,
                db.EmployeeShiftAssignments.Count(assignment =>
                    assignment.ShiftId == shift.Id && assignment.EffectiveTo == null)))
            .ToListAsync(ct);

        return shifts;
    }

    /// <summary>Gets one configured shift pattern.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ShiftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShiftResponse>> Get(Guid id, CancellationToken ct)
    {
        var shift = await db.Shifts.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new ShiftResponse(
                item.Id,
                item.Name,
                item.Category,
                item.StartTime,
                item.EndTime,
                item.BreakMinutes,
                item.GraceMinutes,
                item.CrossesMidnight,
                item.IsActive,
                db.EmployeeShiftAssignments.Count(assignment =>
                    assignment.ShiftId == item.Id && assignment.EffectiveTo == null)))
            .FirstOrDefaultAsync(ct);

        return shift is null ? NotFound() : shift;
    }

    /// <summary>Creates a reusable shift pattern.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageSchedules)]
    [ProducesResponseType(typeof(ShiftResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShiftResponse>> Create(ShiftRequest request, CancellationToken ct)
    {
        if (!TryParseTimes(request, out var start, out var end, out var timeError))
            return BadRequest(new ProblemDetails { Title = timeError });

        var shift = new Shift();
        Apply(shift, request, start, end);
        db.Shifts.Add(shift);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Conflict(new ProblemDetails { Title = "A shift with that name already exists." });
        }

        var response = await GetResponseAsync(shift.Id, ct);
        return CreatedAtAction(nameof(Get), new { id = shift.Id }, response);
    }

    /// <summary>Updates the timing, rules, or active state of a shift pattern.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.ManageSchedules)]
    [ProducesResponseType(typeof(ShiftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShiftResponse>> Update(
        Guid id, ShiftRequest request, CancellationToken ct)
    {
        if (!TryParseTimes(request, out var start, out var end, out var timeError))
            return BadRequest(new ProblemDetails { Title = timeError });

        var shift = await db.Shifts.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (shift is null) return NotFound();

        if (!request.IsActive && shift.IsActive)
        {
            var assignmentCount = await db.EmployeeShiftAssignments
                .CountAsync(assignment => assignment.ShiftId == id && assignment.EffectiveTo == null, ct);
            if (assignmentCount > 0)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "This shift still has assigned employees.",
                    Detail = "Reassign those employees before deactivating the shift."
                });
            }
        }

        Apply(shift, request, start, end);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Conflict(new ProblemDetails { Title = "A shift with that name already exists." });
        }

        return await GetResponseAsync(id, ct);
    }

    /// <summary>Deactivates a shift pattern that has no current employee assignments.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.ManageSchedules)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (shift is null) return NotFound();

        var assignmentCount = await db.EmployeeShiftAssignments
            .CountAsync(assignment => assignment.ShiftId == id && assignment.EffectiveTo == null, ct);
        if (assignmentCount > 0)
        {
            return Conflict(new ProblemDetails
            {
                Title = "This shift still has assigned employees.",
                Detail = "Reassign those employees before deactivating the shift."
            });
        }

        shift.IsActive = false;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Lists active employees and their current recurring shift assignments.</summary>
    [HttpGet("assignments")]
    [ProducesResponseType(typeof(IReadOnlyList<EmployeeShiftResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<EmployeeShiftResponse>>> Assignments(
        [FromQuery] string? search, [FromQuery] Guid? departmentId, CancellationToken ct)
    {
        var query = db.Employees.AsNoTracking()
            .Where(employee => employee.EmploymentStatus == EmploymentStatuses.Active);

        if (departmentId is Guid id)
            query = query.Where(employee => employee.DepartmentId == id);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(employee =>
                EF.Functions.Like(employee.FullName, pattern, "\\") ||
                EF.Functions.Like(employee.EmployeeNumber, pattern, "\\") ||
                EF.Functions.Like(employee.Department.Name, pattern, "\\"));
        }

        var employees = await query.OrderBy(employee => employee.LastName).ThenBy(employee => employee.FirstName)
            .Select(employee => new EmployeeShiftResponse(
                employee.Id,
                employee.EmployeeNumber,
                employee.FullName,
                employee.Department.Name,
                employee.ShiftAssignments.Where(assignment => assignment.EffectiveTo == null)
                    .Select(assignment => (Guid?)assignment.ShiftId).FirstOrDefault(),
                employee.ShiftAssignments.Where(assignment => assignment.EffectiveTo == null)
                    .Select(assignment => assignment.Shift.Name).FirstOrDefault(),
                employee.ShiftAssignments.Where(assignment => assignment.EffectiveTo == null)
                    .Select(assignment => (DateOnly?)assignment.EffectiveFrom).FirstOrDefault()))
            .ToListAsync(ct);

        return employees;
    }

    /// <summary>Changes an employee's recurring shift effective immediately and retains assignment history.</summary>
    [HttpPut("assignments/{employeeId:guid}")]
    [Authorize(Policy = Policies.ManageSchedules)]
    [ProducesResponseType(typeof(EmployeeShiftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EmployeeShiftResponse>> Assign(
        Guid employeeId, AssignShiftRequest request, CancellationToken ct)
    {
        var employee = await db.Employees
            .Include(item => item.ShiftAssignments.Where(assignment => assignment.EffectiveTo == null))
            .FirstOrDefaultAsync(item => item.Id == employeeId &&
                item.EmploymentStatus == EmploymentStatuses.Active, ct);
        if (employee is null) return NotFound();

        var newShift = await db.Shifts.FirstOrDefaultAsync(
            shift => shift.Id == request.ShiftId && shift.IsActive, ct);
        if (newShift is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Select an active shift pattern."
            });
        }

        var today = SiteClock.Today;
        var current = employee.ShiftAssignments.FirstOrDefault();
        if (current?.ShiftId == newShift.Id)
            return await GetAssignmentAsync(employee.Id, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (current is null)
        {
            db.EmployeeShiftAssignments.Add(new EmployeeShiftAssignment
            {
                EmployeeId = employee.Id,
                ShiftId = newShift.Id,
                EffectiveFrom = today
            });
        }
        else if (current.ShiftId != newShift.Id)
        {
            if (current.EffectiveFrom >= today)
            {
                current.ShiftId = newShift.Id;
            }
            else
            {
                current.EffectiveTo = today.AddDays(-1);
                await db.SaveChangesAsync(ct);
                db.EmployeeShiftAssignments.Add(new EmployeeShiftAssignment
                {
                    EmployeeId = employee.Id,
                    ShiftId = newShift.Id,
                    EffectiveFrom = today
                });
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetAssignmentAsync(employee.Id, ct);
    }

    private async Task<ShiftResponse> GetResponseAsync(Guid id, CancellationToken ct) =>
        await db.Shifts.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new ShiftResponse(
                item.Id,
                item.Name,
                item.Category,
                item.StartTime,
                item.EndTime,
                item.BreakMinutes,
                item.GraceMinutes,
                item.CrossesMidnight,
                item.IsActive,
                db.EmployeeShiftAssignments.Count(assignment =>
                    assignment.ShiftId == item.Id && assignment.EffectiveTo == null)))
            .FirstAsync(ct);

    private async Task<EmployeeShiftResponse> GetAssignmentAsync(Guid employeeId, CancellationToken ct) =>
        await db.Employees.AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(employee => new EmployeeShiftResponse(
                employee.Id,
                employee.EmployeeNumber,
                employee.FullName,
                employee.Department.Name,
                employee.ShiftAssignments.Where(assignment => assignment.EffectiveTo == null)
                    .Select(assignment => (Guid?)assignment.ShiftId).FirstOrDefault(),
                employee.ShiftAssignments.Where(assignment => assignment.EffectiveTo == null)
                    .Select(assignment => assignment.Shift.Name).FirstOrDefault(),
                employee.ShiftAssignments.Where(assignment => assignment.EffectiveTo == null)
                    .Select(assignment => (DateOnly?)assignment.EffectiveFrom).FirstOrDefault()))
            .FirstAsync(ct);

    private static void Apply(Shift shift, ShiftRequest request, TimeOnly start, TimeOnly end)
    {
        shift.Name = request.Name.Trim();
        shift.Category = request.Category;
        shift.StartTime = start;
        shift.EndTime = end;
        shift.BreakMinutes = request.BreakMinutes;
        shift.GraceMinutes = request.GraceMinutes;
        shift.CrossesMidnight = request.CrossesMidnight;
        shift.IsActive = request.IsActive;
    }

    private static bool TryParseTimes(
        ShiftRequest request, out TimeOnly start, out TimeOnly end, out string error)
    {
        var validStart = TimeOnly.TryParseExact(request.StartTime, "HH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out start);
        var validEnd = TimeOnly.TryParseExact(request.EndTime, "HH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out end);

        if (!validStart || !validEnd)
        {
            error = "Enter shift times in 24-hour HH:mm format.";
            return false;
        }
        if (start == end)
        {
            error = "Shift start and end times must be different.";
            return false;
        }
        if (request.CrossesMidnight && end > start)
        {
            error = "For an overnight shift, the end time must be earlier than the start time.";
            return false;
        }
        if (!request.CrossesMidnight && end < start)
        {
            error = "Enable the overnight option when the shift ends after midnight.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
            SqliteExtendedErrorCode: 2067
        };

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
