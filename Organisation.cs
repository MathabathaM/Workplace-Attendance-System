namespace Workplace.Data.Entities;

public class Site
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? City { get; set; }
    public string Country { get; set; } = "ZA";
    public string Timezone { get; set; } = "Africa/Johannesburg";
    public DateTimeOffset CreatedAt { get; set; }
}

public class Department
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public class Shift
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "day";   // day | early | late
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int BreakMinutes { get; set; } = 30;
    public int GraceMinutes { get; set; } = 5;
    public bool CrossesMidnight { get; set; }
    public bool IsActive { get; set; } = true;
}
