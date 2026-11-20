using System.ComponentModel.DataAnnotations;

namespace Workplace.Api.Contracts;

/// <summary>Filters and pages the audit trail by date, entity, and search text.</summary>
public sealed class AuditLogQuery
{
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }

    [StringLength(100)]
    public string? EntityType { get; set; }

    [StringLength(200)]
    public string? Search { get; set; }

    [Range(1, 1000000)]
    public int Page { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 25;
}

/// <summary>One read-only audit trail entry.</summary>
public sealed record AuditLogResponse(
    long Id,
    DateTimeOffset CreatedAt,
    string? ActorEmail,
    string Action,
    string EntityType,
    string EntityId,
    string? Changes,
    string? Reason,
    string? IpAddress);

/// <summary>A page of audit trail entries for the selected date range.</summary>
public sealed record AuditLogPageResponse(
    DateOnly FromDate,
    DateOnly ToDate,
    IReadOnlyList<AuditLogResponse> Items,
    int Total,
    int Page,
    int PageSize);
