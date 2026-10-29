using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WebhookApi.Tests;

public class RateLimitTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _f;
    public RateLimitTests(TestFactory f) => _f = f;

    private static Task<HttpResponseMessage> Fire(HttpClient c) =>
        c.PostAsJsonAsync("/events", new { event_type = "x", payload = new { a = 1 } });

    [Fact]
    public async Task The_101st_event_in_an_hour_returns_429_with_retry_after()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var client = _f.ClientFor(key);

        for (var i = 0; i < 100; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await Fire(client)).StatusCode);

        var blocked = await Fire(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.TryGetValues("Retry-After", out var retry));
        Assert.True(int.Parse(retry!.Single()) >= 1);
        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rate limit exceeded: 100 events per hour", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Limit_is_per_tenant()
    {
        var (_, keyA) = await _f.CreateTenantAsync();
        var (_, keyB) = await _f.CreateTenantAsync();
        var a = _f.ClientFor(keyA);
        for (var i = 0; i < 101; i++) await Fire(a);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Fire(a)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Fire(_f.ClientFor(keyB))).StatusCode);
    }

    [Fact]
    public async Task Reading_events_does_not_count_against_the_limit()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var client = _f.ClientFor(key);
        for (var i = 0; i < 150; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Fire(client)).StatusCode);
    }
}
