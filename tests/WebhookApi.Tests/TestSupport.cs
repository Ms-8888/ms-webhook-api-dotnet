using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using WebhookApi.Data;
using WebhookApi.Services;

namespace WebhookApi.Tests;

public class InMemoryQueueStore : IQueueStore
{
    private readonly object _lock = new();
    private readonly Queue<int> _queue = new();
    private readonly Dictionary<int, long> _counters = new();

    public List<int> Snapshot() { lock (_lock) return _queue.ToList(); }

    public Task PushAsync(IEnumerable<int> deliveryIds)
    {
        lock (_lock) foreach (var id in deliveryIds) _queue.Enqueue(id);
        return Task.CompletedTask;
    }

    public Task<int?> PopAsync()
    {
        lock (_lock) return Task.FromResult(_queue.Count > 0 ? (int?)_queue.Dequeue() : null);
    }

    public Task<long> DepthAsync() { lock (_lock) return Task.FromResult((long)_queue.Count); }

    public Task PingAsync() => Task.CompletedTask;

    public Task<RateLimitResult> HitRateLimitAsync(int tenantId)
    {
        lock (_lock)
        {
            _counters[tenantId] = _counters.GetValueOrDefault(tenantId) + 1;
            return Task.FromResult(new RateLimitResult(_counters[tenantId], 3600));
        }
    }
}

public class FakeDnsResolver : IDnsResolver
{
    public Dictionary<string, IPAddress[]> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (Records.TryGetValue(host, out var addresses)) return Task.FromResult(addresses);
        // Everything not listed resolves to a public documentation address.
        return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
    }
}

public class FakeWebhookSender : IWebhookSender
{
    public record Call(string Url, byte[] Body, IDictionary<string, string> Headers);

    public List<Call> Calls { get; } = new();
    public WebhookResult Result { get; set; } = new(true, null);

    public Task<WebhookResult> SendAsync(string url, byte[] body, IDictionary<string, string> headers, CancellationToken ct = default)
    {
        Calls.Add(new Call(url, body, headers));
        return Task.FromResult(Result);
    }
}

/// <summary>
/// Runs the real app against an in-memory SQLite database (not PostgreSQL), with an
/// in-memory queue instead of Redis, a fake DNS resolver, a fake webhook sender and a
/// controllable clock. Background workers are removed so tests drive them directly.
/// </summary>
public class TestFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public InMemoryQueueStore Queue { get; } = new();
    public FakeDnsResolver Dns { get; } = new();
    public FakeWebhookSender Sender { get; } = new();
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public TestFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));

            services.RemoveAll<IQueueStore>();
            services.AddSingleton<IQueueStore>(Queue);
            services.RemoveAll<IDnsResolver>();
            services.AddSingleton<IDnsResolver>(Dns);
            services.RemoveAll<IWebhookSender>();
            services.AddSingleton<IWebhookSender>(Sender);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);

            foreach (var d in services.Where(s => s.ServiceType == typeof(IHostedService)).ToList())
                services.Remove(d);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }

    public async Task<(Tenant Tenant, string ApiKey)> CreateTenantAsync(bool active = true)
    {
        var key = ApiKeys.GenerateToken();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = new Tenant { Name = "Test Tenant", ApiKeyHash = ApiKeys.Hash(key), IsActive = active };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return (tenant, key);
    }

    public HttpClient ClientFor(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        return client;
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
