using System.ComponentModel.DataAnnotations;

namespace Workplace.Api.Contracts;

/// <summary>Filters and pages attendance records for a site-local work date.</summary>
public sealed class AttendanceListQuery
{
    public DateOnly? Date { get; set; }
    public string? Search { get; set; }
    public Guid? DepartmentId { get; set; }

    [RegularExpression("^(on_time|late|absent|incomplete|approved_exception)$")]
    public string? Status { get; set; }

    [Range(1, 1000000)]
    public int Page { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 8;
}

/// <summary>Attendance values to save with a required explanation for the change.</summary>
public sealed record UpdateAttendanceRequest
{
    [Required, RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    public required string ScheduledStart { get; init; }

    [RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    public string? ClockIn { get; init; }

    [RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    public string? ClockOut { get; init; }

    [Required, RegularExpression("^(on_time|late|absent|incomplete|approved_exception)$")]
    public required string Status { get; init; }

    [Required, MinLength(8), MaxLength(500)]
    public required string Reason { get; init; }
}

/// <summary>One employee's attendance record for a work date.</summary>
public sealed record AttendanceRecordResponse(
    Guid Id,
    string EmployeeNumber,
    string EmployeeName,
    string Department,
    TimeOnly ScheduledStart,
    TimeOnly? ClockIn,
    TimeOnly? ClockOut,
    int? WorkedMinutes,
    string Status);

/// <summary>Attendance counts for the selected date and authorized employee scope.</summary>
public sealed record AttendanceSummary(
    int Total,
    int OnTime,
    int Late,
    int Absent,
    int Incomplete,
    int ApprovedException);

/// <summary>A page of attendance records and date summary.</summary>
public sealed record AttendancePageResponse(
    DateOnly Date,
    IReadOnlyList<AttendanceRecordResponse> Items,
    int Total,
    int Page,
    int PageSize,
    AttendanceSummary Summary);

/// <summary>Filters a report to a date range and optional department.</summary>
public sealed class AttendanceReportQuery
{
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    public Guid? DepartmentId { get; set; }
}

/// <summary>Attendance totals for one site-local work date.</summary>
public sealed record AttendanceReportDay(DateOnly Date, AttendanceSummary Summary);

/// <summary>Attendance totals grouped by employee for the selected report range.</summary>
public sealed record EmployeeAttendanceSummary(
    string EmployeeNumber,
    string EmployeeName,
    string Department,
    int Total,
    int OnTime,
    int Late,
    int Absent,
    int Incomplete,
    int ApprovedException);

/// <summary>Attendance trends and employee totals across the selected date range.</summary>
public sealed record AttendanceReportResponse(
    DateOnly FromDate,
    DateOnly ToDate,
    AttendanceSummary Summary,
    IReadOnlyList<AttendanceReportDay> DailyTrend,
    IReadOnlyList<EmployeeAttendanceSummary> Employees);
