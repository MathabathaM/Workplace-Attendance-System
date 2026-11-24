using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Contracts;
using Workplace.Api.Services;
using Workplace.Data;
using Workplace.Data.Auth;
using Workplace.Data.Entities;

namespace Workplace.Api.Controllers;

/// <summary>Provides read-only access to the system audit trail.</summary>
[ApiController]
[Route("api/audit-logs")]
[Authorize(Roles = RoleNames.SystemAdministrator + "," + RoleNames.Auditor)]
public sealed class AuditLogsController(AppDbContext db) : ControllerBase
{
    /// <summary>Returns audit entries for a date range with optional text/entity filters.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AuditLogPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AuditLogPageResponse>> List(
        [FromQuery] AuditLogQuery query, CancellationToken ct)
    {
        var toDate = query.ToDate ?? SiteClock.Today;
        if (query.FromDate is null && toDate.DayNumber < 29)
            return BadRequest(new ProblemDetails { Title = "The selected end date is too early for the default range." });
        var fromDate = query.FromDate ?? toDate.AddDays(-29);
        if (fromDate > toDate)
            return BadRequest(new ProblemDetails { Title = "The start date must be on or before the end date." });
        if (toDate.DayNumber - fromDate.DayNumber > 365)
            return BadRequest(new ProblemDetails { Title = "Audit log searches are limited to 366 days." });
        if (toDate == DateOnly.MaxValue)
            return BadRequest(new ProblemDetails { Title = "The end date must be earlier than the maximum supported date." });

        var rangeStart = ToUtcStartOfDay(fromDate);
        var rangeEnd = ToUtcStartOfDay(toDate.AddDays(1));
        IQueryable<AuditLog> logs = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            ? db.AuditLogs.FromSqlInterpolated(
                $"SELECT * FROM audit_logs WHERE julianday(created_at) >= julianday({rangeStart}) AND julianday(created_at) < julianday({rangeEnd}) ORDER BY julianday(created_at) DESC, id DESC")
            : db.AuditLogs.Where(log => log.CreatedAt >= rangeStart && log.CreatedAt < rangeEnd);
        logs = logs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.EntityType))
            logs = logs.Where(log => log.EntityType == query.EntityType.Trim());

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{EscapeLike(query.Search.Trim().ToLowerInvariant())}%";
            logs = logs.Where(log =>
                EF.Functions.Like(log.Action.ToLower(), pattern, "\\") ||
                EF.Functions.Like(log.EntityType.ToLower(), pattern, "\\") ||
                EF.Functions.Like(log.EntityId.ToLower(), pattern, "\\") ||
                (log.Changes != null && EF.Functions.Like(log.Changes.ToLower(), pattern, "\\")) ||
                (log.Reason != null && EF.Functions.Like(log.Reason.ToLower(), pattern, "\\")) ||
                db.Users.Any(user => user.Id == log.ActorId && user.Email != null &&
                    EF.Functions.Like(user.Email.ToLower(), pattern, "\\")));
        }

        var total = await logs.CountAsync(ct);
        var records = await logs
            .OrderByDescending(log => log.CreatedAt)
            .ThenByDescending(log => log.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(log => new AuditLogProjection(
                log.Id,
                log.CreatedAt,
                db.Users.Where(user => user.Id == log.ActorId)
                    .Select(user => user.Email)
                    .FirstOrDefault(),
                log.Action,
                log.EntityType,
                log.EntityId,
                log.Changes,
                log.Reason,
                log.IpAddress))
            .ToListAsync(ct);

        var items = records.Select(record => new AuditLogResponse(
            record.Id,
            record.CreatedAt,
            record.ActorEmail,
            record.Action,
            record.EntityType,
            record.EntityId,
            record.Changes,
            record.Reason,
            record.IpAddress?.ToString())).ToArray();

        return new AuditLogPageResponse(fromDate, toDate, items, total, query.Page, query.PageSize);
    }

    private static DateTimeOffset ToUtcStartOfDay(DateOnly date)
    {
        if (date == DateOnly.MinValue)
            return DateTimeOffset.MinValue;

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");
        var localMidnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(localMidnight, zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed record AuditLogProjection(
        long Id,
        DateTimeOffset CreatedAt,
        string? ActorEmail,
        string Action,
        string EntityType,
        string EntityId,
        string? Changes,
        string? Reason,
        IPAddress? IpAddress);
}
