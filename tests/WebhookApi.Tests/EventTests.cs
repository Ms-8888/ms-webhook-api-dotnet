using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebhookApi.Data;

namespace WebhookApi.Tests;

public class EventTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _f;
    public EventTests(TestFactory f) => _f = f;

    private static async Task<int> AddEndpoint(HttpClient client, string url)
    {
        var r = await client.PostAsJsonAsync("/endpoints", new { url });
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Fire_event_returns_202_and_queues_one_delivery_per_active_endpoint()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var client = _f.ClientFor(key);
        await AddEndpoint(client, "https://example.com/a");
        await AddEndpoint(client, "https://example.com/b");
        var removed = await AddEndpoint(client, "https://example.com/c");
        await client.DeleteAsync($"/endpoints/{removed}");

        var response = await client.PostAsJsonAsync("/events",
            new { event_type = "order.created", payload = new { order_id = 42 } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("queued_deliveries").GetInt32());
        var eventId = json.GetProperty("event_id").GetInt32();

        var deliveries = await _f.WithDbAsync(db => db.Deliveries.Where(d => d.EventId == eventId).ToListAsync());
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, d =>
        {
            Assert.Equal("pending", d.Status);
            Assert.Equal(0, d.AttemptCount);
        });
        var queued = _f.Queue.Snapshot();
        foreach (var d in deliveries) Assert.Contains(d.Id, queued);
    }

    [Fact]
    public async Task Fire_event_without_endpoints_queues_nothing()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var response = await _f.ClientFor(key).PostAsJsonAsync("/events",
            new { event_type = "x", payload = new { a = 1 } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("queued_deliveries").GetInt32());
    }

    [Fact]
    public async Task Fire_event_requires_event_type_and_object_payload()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var client = _f.ClientFor(key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/events", new { payload = new { a = 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/events", new { event_type = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/events", new { event_type = "x", payload = new[] { 1, 2 } })).StatusCode);
    }

    [Fact]
    public async Task Fire_event_with_bad_key_returns_401()
    {
        var response = await _f.ClientFor("nope").PostAsJsonAsync("/events",
            new { event_type = "x", payload = new { a = 1 } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_events_paginates_by_20_newest_first_and_returns_the_payload_object()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var client = _f.ClientFor(key);
        for (var i = 1; i <= 25; i++)
            await client.PostAsJsonAsync("/events", new { event_type = "n", payload = new { n = i } });

        var page1 = await client.GetFromJsonAsync<JsonElement>("/events");
        var page2 = await client.GetFromJsonAsync<JsonElement>("/events?page=2");

        Assert.Equal(20, page1.GetArrayLength());
        Assert.Equal(5, page2.GetArrayLength());
        Assert.Equal(25, page1[0].GetProperty("payload").GetProperty("n").GetInt32());
        Assert.Equal(1, page2[4].GetProperty("payload").GetProperty("n").GetInt32());
        Assert.Equal("n", page1[0].GetProperty("event_type").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/events?page=0")).StatusCode);
    }

    [Fact]
    public async Task Events_and_deliveries_are_isolated_between_tenants()
    {
        var (_, keyA) = await _f.CreateTenantAsync();
        var (_, keyB) = await _f.CreateTenantAsync();
        var a = _f.ClientFor(keyA);
        var b = _f.ClientFor(keyB);
        await AddEndpoint(a, "https://example.com/a");
        var fired = await (await a.PostAsJsonAsync("/events", new { event_type = "x", payload = new { a = 1 } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var eventId = fired.GetProperty("event_id").GetInt32();

        var otherList = await b.GetFromJsonAsync<JsonElement>("/events");
        var otherDeliveries = await b.GetFromJsonAsync<JsonElement>($"/events/{eventId}/deliveries");

        Assert.Equal(0, otherList.GetArrayLength());
        Assert.Equal(0, otherDeliveries.GetArrayLength());
    }

    [Fact]
    public async Task Get_deliveries_returns_the_documented_fields()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var client = _f.ClientFor(key);
        var endpointId = await AddEndpoint(client, "https://example.com/a");
        var fired = await (await client.PostAsJsonAsync("/events", new { event_type = "x", payload = new { a = 1 } }))
            .Content.ReadFromJsonAsync<JsonElement>();

        var list = await client.GetFromJsonAsync<JsonElement>($"/events/{fired.GetProperty("event_id").GetInt32()}/deliveries");

        Assert.Equal(1, list.GetArrayLength());
        var d = list[0];
        Assert.Equal(endpointId, d.GetProperty("endpoint_id").GetInt32());
        Assert.Equal("https://example.com/a", d.GetProperty("endpoint_url").GetString());
        Assert.Equal("pending", d.GetProperty("status").GetString());
        Assert.Equal(0, d.GetProperty("attempt_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("last_error").ValueKind);
        Assert.Equal(JsonValueKind.Null, d.GetProperty("delivered_at").ValueKind);
        Assert.True(d.TryGetProperty("created_at", out _));
        Assert.True(d.TryGetProperty("id", out _));
    }
}
