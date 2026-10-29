using StackExchange.Redis;

namespace WebhookApi.Services;

public record RateLimitResult(long Count, long TtlSeconds);

/// <summary>Redis-backed delivery queue and per-tenant rate limit counter.</summary>
public interface IQueueStore
{
    Task PushAsync(IEnumerable<int> deliveryIds);
    Task<int?> PopAsync();
    Task<long> DepthAsync();
    Task PingAsync();
    Task<RateLimitResult> HitRateLimitAsync(int tenantId);
}

public class RedisQueueStore : IQueueStore
{
    public const string QueueKey = "deliveries:queue";
    private readonly IConnectionMultiplexer _redis;

    public RedisQueueStore(IConnectionMultiplexer redis) => _redis = redis;

    public async Task PushAsync(IEnumerable<int> deliveryIds)
    {
        var values = deliveryIds.Select(i => (RedisValue)i.ToString()).ToArray();
        if (values.Length == 0) return;
        await _redis.GetDatabase().ListRightPushAsync(QueueKey, values);
    }

    public async Task<int?> PopAsync()
    {
        var value = await _redis.GetDatabase().ListLeftPopAsync(QueueKey);
        return value.HasValue && int.TryParse(value.ToString(), out var id) ? id : null;
    }

    public async Task<long> DepthAsync() => await _redis.GetDatabase().ListLengthAsync(QueueKey);

    public async Task PingAsync() => await _redis.GetDatabase().PingAsync();

    public async Task<RateLimitResult> HitRateLimitAsync(int tenantId)
    {
        // SET NX initializes the key with its TTL; INCR then counts. Both run in one
        // MULTI/EXEC so the key cannot be left without an expiry.
        var key = $"rate:{tenantId}:events";
        var tran = _redis.GetDatabase().CreateTransaction();
        _ = tran.StringSetAsync(key, 0, TimeSpan.FromHours(1), When.NotExists);
        var incr = tran.StringIncrementAsync(key);
        var ttl = tran.KeyTimeToLiveAsync(key);
        await tran.ExecuteAsync();
        var seconds = (await ttl)?.TotalSeconds ?? 3600;
        return new RateLimitResult(await incr, (long)seconds);
    }
}
