namespace Workplace.Api.Contracts;

/// <summary>One employee's assigned shift pattern.</summary>
public sealed record EmployeePortalShiftResponse(
    string Name,
    string Category,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int BreakMinutes,
    bool CrossesMidnight,
    DateOnly EffectiveFrom);

/// <summary>An attendance record visible to its employee.</summary>
public sealed record EmployeeAttendanceResponse(
    DateOnly ShiftDate,
    TimeOnly ScheduledStart,
    TimeOnly? ClockIn,
    TimeOnly? ClockOut,
    int? WorkedMinutes,
    string Status);

/// <summary>The signed-in employee's own profile, schedule, and current attendance.</summary>
public sealed record EmployeePortalResponse(
    string EmployeeNumber,
    string EmployeeName,
    string Department,
    DateOnly Today,
    EmployeePortalShiftResponse? AssignedShift,
    EmployeeAttendanceResponse? TodayAttendance,
    int WorkedMinutesThisWeek,
    IReadOnlyList<EmployeeAttendanceResponse> RecentAttendance);

/// <summary>Pages the signed-in employee's own attendance history.</summary>
public sealed class EmployeeAttendanceQuery
{
    [System.ComponentModel.DataAnnotations.Range(1, 1000000)]
    public int Page { get; set; } = 1;

    [System.ComponentModel.DataAnnotations.Range(1, 100)]
    public int PageSize { get; set; } = 10;
}

/// <summary>A page of the signed-in employee's attendance history.</summary>
public sealed record EmployeeAttendancePageResponse(
    IReadOnlyList<EmployeeAttendanceResponse> Items,
    int Total,
    int Page,
    int PageSize);
