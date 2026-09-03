using CCaaS.Domain.Integration;
using CCaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CCaaS.Infrastructure.Messaging;

/// <summary>
/// Section 13 - Transactional Outbox: "A publisher worker sends pending outbox messages to
/// RabbitMQ and marks them published." Business services write an OutboxMessage row in the
/// SAME SaveChangesAsync call as their business data (see CallService/ConversationService),
/// so this background service is the only thing that ever touches RabbitMQ for domain events.
/// </summary>
public class OutboxPublisherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxPublisherService> _logger;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public OutboxPublisherService(IServiceScopeFactory scopeFactory, ILogger<OutboxPublisherService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishPendingBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox publish loop failed; will retry after the poll interval.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task PublishPendingBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CcaasDbContext>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();

        // IgnoreQueryFilters: outbox rows span all tenants; this worker runs outside any
        // single-tenant request context, so the normal tenant query filter must be bypassed
        // here deliberately (this is the ONE place in the codebase that should do that).
        var pending = await dbContext.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.PublishedAt == null && m.Attempts < 10)
            .OrderBy(m => m.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var message in pending)
        {
            try
            {
                await eventBus.PublishAsync(message.EventType, message.PayloadJson, ct);
                message.PublishedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                message.Attempts += 1;
                message.LastError = ex.Message;
                _logger.LogWarning(ex, "Failed to publish outbox message {Id} (attempt {Attempt})", message.Id, message.Attempts);
            }
        }

        if (pending.Count > 0)
            await dbContext.SaveChangesAsync(ct);
    }
}
