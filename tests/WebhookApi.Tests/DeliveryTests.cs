using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebhookApi.Data;
using WebhookApi.Services;

namespace WebhookApi.Tests;

public class SignatureTests
{
    [Fact]
    public void Matches_an_independently_computed_hmac()
    {
        // Expected value computed with: printf '1700000000.{"order_id": 42}' | openssl dgst -sha256 -hmac secret123
        var payload = Encoding.UTF8.GetBytes("{\"order_id\": 42}");
        var signature = RequestSigner.Sign("secret123", payload, "1700000000");
        Assert.Equal("sha256=b29027bae5c4f6f9c61cd965817c70eed2fdf769938d7c67bfe0e7e6c81543c7", signature);
    }

    [Fact]
    public void Changes_with_secret_timestamp_and_payload()
    {
        var p = Encoding.UTF8.GetBytes("{}");
        var baseline = RequestSigner.Sign("s", p, "1");
        Assert.NotEqual(baseline, RequestSigner.Sign("t", p, "1"));
        Assert.NotEqual(baseline, RequestSigner.Sign("s", p, "2"));
        Assert.NotEqual(baseline, RequestSigner.Sign("s", Encoding.UTF8.GetBytes("{ }"), "1"));
    }

    [Fact]
    public void Api_key_hash_is_sha256_hex()
    {
        // printf 'my-test-key' | openssl dgst -sha256
        Assert.Equal("20eb0536650933acad4ae017f3df774426ea2e4187d13728c99a6baf5e37b2b3", ApiKeys.Hash("my-test-key"));
    }
}

public class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 300)]
    [InlineData(3, 1800)]
    public void Delays_are_1_5_and_30_minutes(int attempt, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), RetryPolicy.DelayAfterFailedAttempt(attempt));
    }

    [Fact]
    public void No_delay_after_the_fourth_attempt()
    {
        Assert.Null(RetryPolicy.DelayAfterFailedAttempt(4));
    }
}

public class DeliveryProcessingTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _f;
    public DeliveryProcessingTests(TestFactory f) => _f = f;

    private async Task<(int DeliveryId, string Secret)> SeedDeliveryAsync(bool endpointActive = true)
    {
        var (tenant, _) = await _f.CreateTenantAsync();
        return await _f.WithDbAsync(async db =>
        {
            var endpoint = new Endpoint
            {
                TenantId = tenant.Id, Url = "https://hooks.example.com/in",
                Secret = ApiKeys.GenerateToken(), IsActive = endpointActive,
            };
            var ev = new Event { TenantId = tenant.Id, EventType = "order.created", Payload = "{\"order_id\": 42}" };
            var delivery = new Delivery { Event = ev, Endpoint = endpoint };
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync();
            return (delivery.Id, endpoint.Secret);
        });
    }

    private Task Process(int id) => _f.WithDbAsync(async db =>
    {
        // A fresh scope per attempt: state must come from the database, not from memory.
        using var scope = _f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessAsync(id);
        return 0;
    });

    private Task<Delivery> Load(int id) => _f.WithDbAsync(db => db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == id));

    [Fact]
    public async Task Successful_delivery_is_marked_delivered_and_sent_with_a_verifiable_signature()
    {
        var (id, secret) = await SeedDeliveryAsync();
        _f.Sender.Calls.Clear();
        _f.Sender.Result = new WebhookResult(true, null);

        await Process(id);

        var d = await Load(id);
        Assert.Equal("delivered", d.Status);
        Assert.Equal(1, d.AttemptCount);
        Assert.NotNull(d.DeliveredAt);
        Assert.Null(d.NextRetryAt);

        var call = _f.Sender.Calls.Single();
        Assert.Equal("https://hooks.example.com/in", call.Url);
        Assert.Equal("order.created", call.Headers["X-Event-Type"]);
        var timestamp = call.Headers["X-Webhook-Timestamp"];
        var expected = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + ".").Concat(call.Body).ToArray()))
            .ToLowerInvariant();
        Assert.Equal(expected, call.Headers["X-Webhook-Signature"]);
        Assert.Equal("{\"order_id\": 42}", Encoding.UTF8.GetString(call.Body));
    }

    [Fact]
    public async Task Failures_are_rescheduled_at_1_5_and_30_minutes_then_marked_failed()
    {
        var (id, _) = await SeedDeliveryAsync();
        _f.Sender.Result = new WebhookResult(false, "HTTP 500 Internal Server Error");

        var expectedDelays = new[] { 60, 300, 1800 };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var before = _f.Time.GetUtcNow().UtcDateTime;
            await Process(id);
            var d = await Load(id);
            Assert.Equal("pending", d.Status);
            Assert.Equal(attempt, d.AttemptCount);
            Assert.Equal(before.AddSeconds(expectedDelays[attempt - 1]), d.NextRetryAt);
            Assert.Equal($"HTTP 500 Internal Server Error — attempt {attempt} of 4", d.LastError);
            _f.Time.Advance(TimeSpan.FromSeconds(expectedDelays[attempt - 1]));
        }

        await Process(id);
        var final = await Load(id);
        Assert.Equal("failed", final.Status);
        Assert.Equal(4, final.AttemptCount);
        Assert.Null(final.NextRetryAt);
        Assert.Contains("attempt 4 of 4", final.LastError);

        // A failed delivery is never attempted again.
        var calls = _f.Sender.Calls.Count;
        await Process(id);
        Assert.Equal(calls, _f.Sender.Calls.Count);
    }

    [Fact]
    public async Task A_retry_after_failure_can_succeed()
    {
        var (id, _) = await SeedDeliveryAsync();
        _f.Sender.Result = new WebhookResult(false, "Connection timeout after 10000ms");
        await Process(id);
        _f.Sender.Result = new WebhookResult(true, null);
        await Process(id);

        var d = await Load(id);
        Assert.Equal("delivered", d.Status);
        Assert.Equal(2, d.AttemptCount);
    }

    [Fact]
    public async Task Inactive_endpoint_fails_the_delivery_without_sending()
    {
        var (id, _) = await SeedDeliveryAsync(endpointActive: false);
        _f.Sender.Calls.Clear();

        await Process(id);

        var d = await Load(id);
        Assert.Equal("failed", d.Status);
        Assert.Equal("Endpoint no longer active", d.LastError);
        Assert.Empty(_f.Sender.Calls);
    }

    [Fact]
    public async Task Retry_scanner_requeues_only_due_pending_deliveries_and_does_not_double_queue()
    {
        await _f.WithDbAsync(async db => { await db.Deliveries.ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, "failed")); return 0; });
        var (due, _) = await SeedDeliveryAsync();
        var (notDue, _) = await SeedDeliveryAsync();
        var (exhausted, _) = await SeedDeliveryAsync();
        var now = _f.Time.GetUtcNow().UtcDateTime;
        await _f.WithDbAsync(async db =>
        {
            var rows = await db.Deliveries.Where(d => d.Id == due || d.Id == notDue || d.Id == exhausted).ToListAsync();
            foreach (var r in rows)
            {
                r.AttemptCount = r.Id == exhausted ? 4 : 1;
                r.NextRetryAt = r.Id == notDue ? now.AddMinutes(10) : now.AddSeconds(-1);
            }
            await db.SaveChangesAsync();
            return 0;
        });
        while (await _f.Queue.PopAsync() is not null) { }

        int first, second;
        using (var scope = _f.Services.CreateScope())
            first = await scope.ServiceProvider.GetRequiredService<RetryScanner>().RequeueDueAsync();
        using (var scope = _f.Services.CreateScope())
            second = await scope.ServiceProvider.GetRequiredService<RetryScanner>().RequeueDueAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(new[] { due }, _f.Queue.Snapshot());
    }

    [Fact]
    public async Task Retry_schedule_is_read_from_the_database_by_a_new_scanner_instance_after_a_restart()
    {
        // Simulates a restart: nothing is held in memory between the failed attempt and the scan.
        await _f.WithDbAsync(async db => { await db.Deliveries.ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, "failed")); return 0; });
        var (id, _) = await SeedDeliveryAsync();
        _f.Sender.Result = new WebhookResult(false, "boom");
        await Process(id);
        while (await _f.Queue.PopAsync() is not null) { }

        _f.Time.Advance(TimeSpan.FromSeconds(61));
        using var scope = _f.Services.CreateScope();
        var requeued = await scope.ServiceProvider.GetRequiredService<RetryScanner>().RequeueDueAsync();

        Assert.Equal(1, requeued);
        Assert.Contains(id, _f.Queue.Snapshot());
    }
}
