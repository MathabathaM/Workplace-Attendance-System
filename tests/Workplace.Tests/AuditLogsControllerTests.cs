using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Contracts;
using Workplace.Api.Controllers;
using Workplace.Api.Services;
using Workplace.Data;
using Workplace.Data.Entities;

namespace Workplace.Tests;

public sealed class AuditLogsControllerTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _actorId = Guid.NewGuid();
    private AppDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        _db = new AppDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _db.Users.Add(new AppUser
        {
            Id = _actorId,
            UserName = "auditor@example.com",
            NormalizedUserName = "AUDITOR@EXAMPLE.COM",
            Email = "auditor@example.com",
            NormalizedEmail = "AUDITOR@EXAMPLE.COM"
        });

        var now = DateTimeOffset.UtcNow;
        _db.AuditLogs.AddRange(
            Log("employee.update", "employee", "EMP-1", "{\"name\":\"Alex\"}", "Updated Alex profile", now),
            Log("employee.update", "employee", "EMP-2", "{\"name\":\"Mia\"}", "Updated Mia profile", now.AddMinutes(-1)),
            Log("shift.update", "shift", "SHIFT-1", "{\"startTime\":\"08:30\"}", "Changed shift start", now),
            Log("employee.create", "employee", "EMP-3", "{\"name\":\"Sam\"}", "Created Sam profile", now.AddDays(-2)));
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task List_filters_by_date_entity_and_search_and_includes_actor_details()
    {
        var controller = new AuditLogsController(_db);
        var today = SiteClock.Today;

        var result = await controller.List(new AuditLogQuery
        {
            FromDate = today,
            ToDate = today,
            EntityType = "employee",
            Search = "Alex"
        }, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(1, result.Value.Total);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal("auditor@example.com", item.ActorEmail);
        Assert.Equal("EMP-1", item.EntityId);
        Assert.Equal("Updated Alex profile", item.Reason);
        Assert.Equal("127.0.0.1", item.IpAddress);
    }

    [Fact]
    public async Task List_pages_audit_entries_in_reverse_chronological_order()
    {
        var controller = new AuditLogsController(_db);

        var firstPage = await controller.List(new AuditLogQuery
        {
            FromDate = SiteClock.Today,
            ToDate = SiteClock.Today,
            EntityType = "employee",
            Page = 1,
            PageSize = 1
        }, CancellationToken.None);
        var secondPage = await controller.List(new AuditLogQuery
        {
            FromDate = SiteClock.Today,
            ToDate = SiteClock.Today,
            EntityType = "employee",
            Page = 2,
            PageSize = 1
        }, CancellationToken.None);

        Assert.NotNull(firstPage.Value);
        Assert.Equal(2, firstPage.Value.Total);
        Assert.Equal("EMP-1", Assert.Single(firstPage.Value.Items).EntityId);
        Assert.NotNull(secondPage.Value);
        Assert.Equal("EMP-2", Assert.Single(secondPage.Value.Items).EntityId);
    }

    [Fact]
    public async Task List_rejects_a_start_date_after_the_end_date()
    {
        var controller = new AuditLogsController(_db);
        var today = SiteClock.Today;

        var result = await controller.List(new AuditLogQuery
        {
            FromDate = today,
            ToDate = today.AddDays(-1)
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private AuditLog Log(
        string action, string entityType, string entityId,
        string changes, string reason, DateTimeOffset createdAt) =>
        new()
        {
            ActorId = _actorId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Changes = changes,
            Reason = reason,
            IpAddress = IPAddress.Loopback,
            CreatedAt = createdAt
        };
}
