using System.Text.Json;
using CCaaS.Domain.Calls;
using CCaaS.Infrastructure.Persistence;
using CCaaS.Infrastructure.Telephony;
using Microsoft.EntityFrameworkCore;
using Xunit;

public class PendingVoiceTurnsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completed_turn_is_excluded_for_both_guid_formats(bool legacy)
    {
        var tenant = Guid.NewGuid(); var call = new CallSession { Id = Guid.NewGuid(), TenantId = tenant };
        var source = new CallEvent { Id = Guid.NewGuid(), TenantId = tenant, CallSessionId = call.Id, EventType = "AwaitingVoiceBridge" };
        var done = new CallEvent { TenantId = tenant, CallSessionId = call.Id, EventType = "LocalAiTurnProcessed",
            PayloadJson = legacy ? JsonSerializer.Serialize(new { sourceEventId = source.Id.ToString("N") })
                                 : JsonSerializer.Serialize(new { sourceEventId = source.Id }) };
        Assert.Empty(PendingVoiceTurns.Query(new[] { source, done }.AsQueryable(), new[] { call }.AsQueryable()));
        done.TenantId = Guid.NewGuid();
        Assert.Single(PendingVoiceTurns.Query(new[] { source, done }.AsQueryable(), new[] { call }.AsQueryable()));
        call.EndedAt = DateTime.UtcNow;
        Assert.Empty(PendingVoiceTurns.Query(new[] { source, done }.AsQueryable(), new[] { call }.AsQueryable()));
    }

    [Fact]
    public void Sql_server_can_translate_correlated_pending_query()
    {
        using var db = new CcaasDbContext(new DbContextOptionsBuilder<CcaasDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true;TrustServerCertificate=true").Options);
        // Generates SQL only; no server or credentials needed.
        var sql = PendingVoiceTurns.Query(db.CallEvents.IgnoreQueryFilters(), db.CallSessions.IgnoreQueryFilters())
            .OrderByDescending(x => x.OccurredAt).Take(100).ToQueryString();
        Assert.Contains("NOT EXISTS", sql);
        Assert.Contains("REPLACE", sql);
    }
}
