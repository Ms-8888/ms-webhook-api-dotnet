using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebhookApi.Services;

namespace WebhookApi.Tests;

public class EndpointTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _f;
    public EndpointTests(TestFactory f) => _f = f;

    [Fact]
    public async Task Create_endpoint_returns_201_with_snake_case_fields_and_no_secret()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var client = _f.ClientFor(key);

        var response = await client.PostAsJsonAsync("/endpoints",
            new { url = "https://example.com/webhook", description = "Test endpoint" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://example.com/webhook", json.GetProperty("url").GetString());
        Assert.Equal("Test endpoint", json.GetProperty("description").GetString());
        Assert.True(json.GetProperty("is_active").GetBoolean());
        Assert.True(json.GetProperty("id").GetInt32() > 0);
        Assert.True(json.TryGetProperty("created_at", out var createdAt));
        Assert.EndsWith("Z", createdAt.GetString());
        Assert.False(json.TryGetProperty("secret", out _));
        Assert.Equal(5, json.EnumerateObject().Count());
    }

    [Fact]
    public async Task List_returns_only_own_active_endpoints_newest_first()
    {
        var (_, keyA) = await _f.CreateTenantAsync();
        var (_, keyB) = await _f.CreateTenantAsync();
        var a = _f.ClientFor(keyA);
        var b = _f.ClientFor(keyB);

        var first = await (await a.PostAsJsonAsync("/endpoints", new { url = "https://example.com/one" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        await Task.Delay(5);
        var second = await (await a.PostAsJsonAsync("/endpoints", new { url = "https://example.com/two" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        await b.PostAsJsonAsync("/endpoints", new { url = "https://example.com/other-tenant" });

        var list = await a.GetFromJsonAsync<JsonElement>("/endpoints");
        var ids = list.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(new[] { second.GetProperty("id").GetInt32(), first.GetProperty("id").GetInt32() }, ids);
    }

    [Fact]
    public async Task Delete_deactivates_endpoint_and_hides_it_from_the_list()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var client = _f.ClientFor(key);
        var created = await (await client.PostAsJsonAsync("/endpoints", new { url = "https://example.com/delete-me" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        var delete = await client.DeleteAsync($"/endpoints/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>("/endpoints");
        Assert.DoesNotContain(list.EnumerateArray(), e => e.GetProperty("id").GetInt32() == id);

        // soft delete: the row is still there
        var stillThere = await _f.WithDbAsync(db => db.Endpoints.AnyAsync(e => e.Id == id && !e.IsActive));
        Assert.True(stillThere);
    }

    [Fact]
    public async Task Delete_of_another_tenants_endpoint_returns_404()
    {
        var (_, keyA) = await _f.CreateTenantAsync();
        var (_, keyB) = await _f.CreateTenantAsync();
        var created = await (await _f.ClientFor(keyA).PostAsJsonAsync("/endpoints", new { url = "https://example.com/a" }))
            .Content.ReadFromJsonAsync<JsonElement>();

        var response = await _f.ClientFor(keyB).DeleteAsync($"/endpoints/{created.GetProperty("id").GetInt32()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Endpoint not found", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Missing_api_key_returns_422()
    {
        var response = await _f.CreateClient().GetAsync("/endpoints");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_api_key_returns_401_with_detail()
    {
        var response = await _f.ClientFor("wrong").GetAsync("/endpoints");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid API key", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Inactive_tenant_is_rejected()
    {
        var (_, key) = await _f.CreateTenantAsync(active: false);
        var response = await _f.ClientFor(key).GetAsync("/endpoints");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_key_is_stored_as_sha256_hash_not_plaintext()
    {
        var (tenant, key) = await _f.CreateTenantAsync();
        var stored = await _f.WithDbAsync(db => db.Tenants.Where(t => t.Id == tenant.Id).Select(t => t.ApiKeyHash).SingleAsync());
        Assert.NotEqual(key, stored);
        Assert.Equal(64, stored.Length);
        Assert.Equal(ApiKeys.Hash(key), stored);
    }

    [Fact]
    public async Task Invalid_body_returns_422()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var client = _f.ClientFor(key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/endpoints", new { description = "no url" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/endpoints", new { url = "not a url" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsync("/endpoints", new StringContent("{broken", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Cors_preflight_allows_the_dashboard_origin()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/endpoints");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-api-key,content-type");

        var response = await _f.CreateClient().SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origin));
        Assert.Equal("http://localhost:5173", origin!.Single());
    }
}
