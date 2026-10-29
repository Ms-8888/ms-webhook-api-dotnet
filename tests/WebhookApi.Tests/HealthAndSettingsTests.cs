using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WebhookApi.Services;

namespace WebhookApi.Tests;

public class HealthAndMetricsTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _f;
    public HealthAndMetricsTests(TestFactory f) => _f = f;

    [Fact]
    public async Task Health_reports_ok_without_an_api_key()
    {
        var response = await _f.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal("ok", json.GetProperty("db").GetString());
        Assert.Equal("ok", json.GetProperty("redis").GetString());
    }

    [Fact]
    public async Task Metrics_requires_a_key_and_returns_the_documented_shape()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.ClientFor("bad").GetAsync("/metrics")).StatusCode);

        var (_, key) = await _f.CreateTenantAsync();
        var client = _f.ClientFor(key);
        await client.PostAsJsonAsync("/endpoints", new { url = "https://example.com/a" });
        await client.PostAsJsonAsync("/events", new { event_type = "x", payload = new { a = 1 } });

        var json = await client.GetFromJsonAsync<JsonElement>("/metrics");

        Assert.Equal(1, json.GetProperty("total_events_today").GetInt32());
        Assert.Equal(0.0, json.GetProperty("delivery_success_rate").GetDouble());
        Assert.True(json.GetProperty("queue_depth").GetInt64() >= 1);
        Assert.Equal(0, json.GetProperty("failed_deliveries_24h").GetInt32());
    }

    [Fact]
    public async Task Metrics_success_rate_is_1_when_there_are_no_deliveries()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var json = await _f.ClientFor(key).GetFromJsonAsync<JsonElement>("/metrics");
        Assert.Equal(1.0, json.GetProperty("delivery_success_rate").GetDouble());
    }

    [Fact]
    public async Task Metrics_chart_returns_24_hourly_points()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var json = await _f.ClientFor(key).GetFromJsonAsync<JsonElement>("/metrics/chart");
        Assert.Equal(24, json.GetArrayLength());
        Assert.Matches(@"^\d\d:00$", json[0].GetProperty("hour").GetString()!);
        Assert.Equal(1.0, json[0].GetProperty("rate").GetDouble());
    }
}

public class SettingsTests
{
    [Fact]
    public void Python_style_database_url_is_converted_to_an_npgsql_connection_string()
    {
        var cs = Settings.ToNpgsqlConnectionString("postgresql+asyncpg://postgres:p%40ss@db:5432/webhooks?sslmode=require");
        Assert.Equal("Host=db;Port=5432;Database=webhooks;Username=postgres;Password=p@ss;SSL Mode=Require", cs);
    }

    [Fact]
    public void Plain_npgsql_connection_string_is_passed_through()
    {
        const string cs = "Host=x;Database=y;Username=z;Password=w";
        Assert.Equal(cs, Settings.ToNpgsqlConnectionString(cs));
    }

    [Fact]
    public void Redis_url_is_parsed_into_host_port_and_password()
    {
        var o = Settings.ToRedisOptions("redis://:secret@cache:6380");
        var endpoint = Assert.IsType<System.Net.DnsEndPoint>(o.EndPoints.Single());
        Assert.Equal("cache", endpoint.Host);
        Assert.Equal(6380, endpoint.Port);
        Assert.Equal("secret", o.Password);
        Assert.False(o.Ssl);
        Assert.False(o.AbortOnConnectFail);
        Assert.True(Settings.ToRedisOptions("rediss://cache").Ssl);
    }
}
