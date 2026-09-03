using StackExchange.Redis;

namespace CCaaS.Infrastructure.Caching;

// Section 6 - "Redis: Agent presence, queue snapshots, cache, locks, SignalR scale-out later."
public interface IPresenceCache
{
    Task SetAgentPresenceAsync(Guid tenantId, Guid agentId, string presence, TimeSpan ttl, CancellationToken ct = default);
    Task<string?> GetAgentPresenceAsync(Guid tenantId, Guid agentId, CancellationToken ct = default);
    Task<bool> TryAcquireLockAsync(string lockKey, TimeSpan ttl, CancellationToken ct = default);
    Task ReleaseLockAsync(string lockKey, CancellationToken ct = default);
}

public class RedisPresenceCache : IPresenceCache
{
    private readonly IConnectionMultiplexer _redis;

    public RedisPresenceCache(IConnectionMultiplexer redis) => _redis = redis;

    private static string Key(Guid tenantId, Guid agentId) => $"presence:{tenantId}:{agentId}";

    public async Task SetAgentPresenceAsync(Guid tenantId, Guid agentId, string presence, TimeSpan ttl, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        await db.StringSetAsync(Key(tenantId, agentId), presence, ttl);
    }

    public async Task<string?> GetAgentPresenceAsync(Guid tenantId, Guid agentId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync(Key(tenantId, agentId));
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    /// <summary>Simple distributed lock, e.g. for "only one worker processes this campaign batch at a time".</summary>
    public async Task<bool> TryAcquireLockAsync(string lockKey, TimeSpan ttl, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        return await db.StringSetAsync($"lock:{lockKey}", Environment.MachineName, ttl, When.NotExists);
    }

    public async Task ReleaseLockAsync(string lockKey, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        await db.KeyDeleteAsync($"lock:{lockKey}");
    }
}
