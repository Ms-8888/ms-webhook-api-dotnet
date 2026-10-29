using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebhookApi.Data;
using WebhookApi.Services;

namespace WebhookApi.Api;

public static class RequestHelpers
{
    public const int RateLimitPerHour = 100;

    /// <summary>X-API-Key lookup by SHA-256 hash. 422 if the header is missing, 401 if unknown or inactive.</summary>
    public static async Task<Tenant> AuthenticateAsync(HttpContext ctx, AppDbContext db)
    {
        if (!ctx.Request.Headers.TryGetValue("X-API-Key", out var values) || string.IsNullOrEmpty(values.ToString()))
        {
            throw new ApiException(422, new[]
            {
                new { type = "missing", loc = new[] { "header", "x-api-key" }, msg = "Field required" }
            });
        }

        var hash = ApiKeys.Hash(values.ToString());
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.ApiKeyHash == hash && t.IsActive, ctx.RequestAborted);
        return tenant ?? throw new ApiException(401, "Invalid API key");
    }

    public static async Task<Tenant> AuthenticateWithRateLimitAsync(HttpContext ctx, AppDbContext db, IQueueStore queue)
    {
        var tenant = await AuthenticateAsync(ctx, db);
        var hit = await queue.HitRateLimitAsync(tenant.Id);
        if (hit.Count > RateLimitPerHour)
        {
            throw new ApiException(429, "Rate limit exceeded: 100 events per hour",
                new Dictionary<string, string> { ["Retry-After"] = Math.Max(hit.TtlSeconds, 1).ToString() });
        }
        return tenant;
    }

    public static async Task<JsonElement> ReadJsonObjectAsync(HttpRequest request)
    {
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            throw new ApiException(422, new[]
            {
                new { type = "json_invalid", loc = new[] { "body" }, msg = "JSON decode error" }
            });
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(422, new[]
            {
                new { type = "model_attributes_type", loc = new[] { "body" }, msg = "Input should be a valid dictionary or object" }
            });
        }
        return doc.RootElement;
    }

    public static string RequireString(JsonElement body, string name, int maxLength)
    {
        if (!body.TryGetProperty(name, out var value))
            throw new ApiException(422, new[] { new { type = "missing", loc = new[] { "body", name }, msg = "Field required" } });
        if (value.ValueKind != JsonValueKind.String)
            throw ApiException.Validation("body", name, "Input should be a valid string");
        var s = value.GetString()!;
        if (s.Length > maxLength)
            throw ApiException.Validation("body", name, $"String should have at most {maxLength} characters");
        return s;
    }

    public static string? OptionalString(JsonElement body, string name, int maxLength)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw ApiException.Validation("body", name, "Input should be a valid string");
        var s = value.GetString()!;
        if (s.Length > maxLength)
            throw ApiException.Validation("body", name, $"String should have at most {maxLength} characters");
        return s;
    }
}
