using System.Net;

namespace Workplace.Data.Entities;

/// <summary>Audit records written by the employee change interceptor.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";

    /// <summary>JSON describing what changed.</summary>
    public string? Changes { get; set; }

    public string? Reason { get; set; }
    public IPAddress? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
