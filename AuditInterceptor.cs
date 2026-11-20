using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Workplace.Data.Auth;
using Workplace.Data.Entities;

namespace Workplace.Data.Auditing;

/// <summary>
/// Writes an audit_logs row for every employee, shift, or shift-assignment change, inside
/// the SAME SaveChanges call (same transaction) as the change itself, so a change
/// can never be saved without its audit entry.
/// </summary>
public sealed class AuditInterceptor(ICurrentUser currentUser) : SaveChangesInterceptor
{
    private static readonly HashSet<string> IgnoredProperties = ["Id", "FullName", "CreatedAt", "UpdatedAt"];

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        AddAuditRows(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        AddAuditRows(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void AddAuditRows(DbContext? context)
    {
        if (context is null) return;

        var entries = context.ChangeTracker.Entries()
            .Where(e => e.Entity is Employee or EmployeeShiftAssignment or Shift)
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in entries)
        {
            var changes = new Dictionary<string, object?>();
            foreach (var property in entry.Properties)
            {
                if (IgnoredProperties.Contains(property.Metadata.Name)) continue;
                var name = JsonNamingPolicy.CamelCase.ConvertName(property.Metadata.Name);

                switch (entry.State)
                {
                    case EntityState.Added:
                        changes[name] = property.CurrentValue;
                        break;
                    case EntityState.Deleted:
                        changes[name] = property.OriginalValue;
                        break;
                    case EntityState.Modified
                        when property.IsModified && !Equals(property.OriginalValue, property.CurrentValue):
                        changes[name] = new { from = property.OriginalValue, to = property.CurrentValue };
                        break;
                }
            }

            if (entry.State == EntityState.Modified && changes.Count == 0) continue;

            var verb = entry.State switch
            {
                EntityState.Added => "create",
                EntityState.Deleted => "delete",
                _ => "update"
            };
            var (entityType, actionPrefix) = entry.Entity switch
            {
                Employee => ("employee", "employee"),
                EmployeeShiftAssignment => ("employee_shift_assignment", "employee_shift"),
                Shift => ("shift", "shift"),
                _ => throw new InvalidOperationException("Unsupported audited entity.")
            };

            context.Set<AuditLog>().Add(new AuditLog
            {
                ActorId = currentUser.UserId,
                Action = $"{actionPrefix}.{verb}",
                EntityType = entityType,
                EntityId = entry.Property("Id").CurrentValue?.ToString() ?? "",
                Changes = JsonSerializer.Serialize(changes),
                IpAddress = currentUser.IpAddress
            });
        }
    }
}
