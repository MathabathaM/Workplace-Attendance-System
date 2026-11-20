namespace Workplace.Data.Entities;

public static class AttendanceStatuses
{
    public const string OnTime = "on_time";
    public const string Late = "late";
    public const string Absent = "absent";
    public const string Incomplete = "incomplete";
    public const string ApprovedException = "approved_exception";
}

public class AttendanceRecord
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly ShiftDate { get; set; }
    public TimeOnly ScheduledStart { get; set; }
    public TimeOnly? ClockIn { get; set; }
    public TimeOnly? ClockOut { get; set; }
    public string Status { get; set; } = AttendanceStatuses.OnTime;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Employee Employee { get; set; } = null!;
}
