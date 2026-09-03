using System.Text.Json;
using CCaaS.Domain.Calls;
using CCaaS.Domain.Organization;
using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCaaS.Workers.Telephony;

/// <summary>Closes orphaned database calls left behind by a worker/container interruption.</summary>
public sealed class CallLifecycleReconciler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CallLifecycleReconciler> _logger;

    public CallLifecycleReconciler(IServiceScopeFactory scopeFactory, IConfiguration configuration,
        ILogger<CallLifecycleReconciler> logger)
    { _scopeFactory = scopeFactory; _configuration = configuration; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { _logger.LogWarning(ex, "Stale call lifecycle reconciliation failed; it will retry."); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        var staleMinutes = Math.Clamp(_configuration.GetValue("Telephony:StaleCallMinutes", 30), 5, 1440);
        var cutoff = DateTime.UtcNow.AddMinutes(-staleMinutes);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
        var stale = await db.CallSessions.IgnoreQueryFilters()
            .Where(x => !x.IsDeleted && x.EndedAt == null && x.UpdatedAt < cutoff)
            .Take(100).ToListAsync(ct);
        if (stale.Count == 0) return;

        foreach (var call in stale)
        {
            call.EndedAt = DateTime.UtcNow;
            call.Status = CallStatus.Abandoned;
            call.HangupCause = "Stale lifecycle timeout";
            db.CallEvents.Add(new CallEvent { TenantId = call.TenantId, CallSessionId = call.Id,
                EventType = "StaleCallReconciled", PayloadJson = JsonSerializer.Serialize(new { staleMinutes }) });
            if (call.AgentId is not null)
            {
                var agent = await db.Agents.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == call.AgentId
                    && x.TenantId == call.TenantId && !x.IsDeleted, ct);
                if (agent is not null)
                { agent.Presence = AgentPresence.Available; agent.PresenceChangedAt = DateTime.UtcNow; }
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogWarning("Reconciled {Count} stale call sessions.", stale.Count);
    }
}
