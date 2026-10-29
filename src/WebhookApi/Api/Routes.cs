using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebhookApi.Data;
using Endpoint = WebhookApi.Data.Endpoint;
using WebhookApi.Services;
using static WebhookApi.Api.RequestHelpers;

namespace WebhookApi.Api;

public static class Routes
{
    public static void MapWebhookRoutes(this WebApplication app)
    {
        MapEndpoints(app);
        MapEvents(app);
        MapHealth(app);
    }

    private static void MapEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/endpoints");

        group.MapPost("", async (HttpContext ctx, AppDbContext db, IDnsResolver dns) =>
        {
            var tenant = await AuthenticateAsync(ctx, db);
            var body = await ReadJsonObjectAsync(ctx.Request);
            var url = RequireString(body, "url", 2048);
            var description = OptionalString(body, "description", 500);

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                throw ApiException.Validation("body", "url", "Input should be a valid URL with scheme http or https");

            var error = await SsrfGuard.ValidateUrlAsync(url, dns, ctx.RequestAborted);
            if (error != null)
                throw new ApiException(422, error);

            var endpoint = new Endpoint
            {
                TenantId = tenant.Id,
                Url = url,
                Description = description,
                Secret = ApiKeys.GenerateToken(),
            };
            db.Endpoints.Add(endpoint);
            await db.SaveChangesAsync();
            return Results.Json(EndpointOut.From(endpoint), statusCode: 201);
        });

        group.MapGet("", async (HttpContext ctx, AppDbContext db) =>
        {
            var tenant = await AuthenticateAsync(ctx, db);
            var rows = await db.Endpoints
                .Where(e => e.TenantId == tenant.Id && e.IsActive)
                .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
                .ToListAsync();
            return rows.Select(EndpointOut.From).ToList();
        });

        group.MapDelete("/{endpointId:int}", async (int endpointId, HttpContext ctx, AppDbContext db) =>
        {
            var tenant = await AuthenticateAsync(ctx, db);
            var endpoint = await db.Endpoints.FirstOrDefaultAsync(e => e.Id == endpointId);
            if (endpoint is null || endpoint.TenantId != tenant.Id)
                throw new ApiException(404, "Endpoint not found");
            endpoint.IsActive = false;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static void MapEvents(WebApplication app)
    {
        var group = app.MapGroup("/events");

        group.MapPost("", async (HttpContext ctx, AppDbContext db, IQueueStore queue) =>
        {
            var tenant = await AuthenticateWithRateLimitAsync(ctx, db, queue);
            var body = await ReadJsonObjectAsync(ctx.Request);
            var eventType = RequireString(body, "event_type", 255);
            if (!body.TryGetProperty("payload", out var payload))
                throw new ApiException(422, new[] { new { type = "missing", loc = new[] { "body", "payload" }, msg = "Field required" } });
            if (payload.ValueKind != JsonValueKind.Object)
                throw ApiException.Validation("body", "payload", "Input should be a valid dictionary");

            var ev = new Event { TenantId = tenant.Id, EventType = eventType, Payload = payload.GetRawText() };
            var endpoints = await db.Endpoints
                .Where(e => e.TenantId == tenant.Id && e.IsActive)
                .ToListAsync();
            var deliveries = endpoints.Select(e => new Delivery { Event = ev, EndpointId = e.Id }).ToList();

            db.Events.Add(ev);
            db.Deliveries.AddRange(deliveries);
            await db.SaveChangesAsync();

            // Push to the queue only after the commit so a worker never sees an unsaved row.
            if (deliveries.Count > 0)
                await queue.PushAsync(deliveries.Select(d => d.Id));

            return Results.Json(new FiredEventOut(ev.Id, deliveries.Count), statusCode: 202);
        });

        group.MapGet("", async (HttpContext ctx, AppDbContext db, [FromQuery] int page = 1) =>
        {
            var tenant = await AuthenticateAsync(ctx, db);
            if (page < 1)
                throw ApiException.Validation("query", "page", "Input should be greater than or equal to 1");
            const int pageSize = 20;
            var rows = await db.Events
                .Where(e => e.TenantId == tenant.Id)
                .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .ToListAsync();
            return rows.Select(EventOut.From).ToList();
        });

        group.MapGet("/{eventId:int}/deliveries", async (int eventId, HttpContext ctx, AppDbContext db) =>
        {
            var tenant = await AuthenticateAsync(ctx, db);
            var rows = await db.Deliveries
                .Where(d => d.EventId == eventId && d.Event!.TenantId == tenant.Id)
                .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
                .Select(d => new DeliveryOut(d.Id, d.EndpointId, d.Endpoint!.Url, d.Status, d.AttemptCount,
                    d.LastError, d.DeliveredAt, d.CreatedAt))
                .ToListAsync();
            return rows;
        });
    }

    private static void MapHealth(WebApplication app)
    {
        app.MapGet("/health", async (AppDbContext db, IQueueStore queue) =>
        {
            var dbStatus = "ok";
            var redisStatus = "ok";
            try { await db.Database.ExecuteSqlRawAsync("SELECT 1"); } catch { dbStatus = "error"; }
            try { await queue.PingAsync(); } catch { redisStatus = "error"; }
            var overall = dbStatus == "ok" && redisStatus == "ok" ? "ok" : "degraded";
            return new HealthOut(overall, dbStatus, redisStatus);
        });

        app.MapGet("/metrics", async (HttpContext ctx, AppDbContext db, IQueueStore queue, TimeProvider time) =>
        {
            var tenant = await AuthenticateAsync(ctx, db);
            var dayAgo = time.GetUtcNow().UtcDateTime.AddHours(-24);

            var totalEvents = await db.Events.CountAsync(e => e.TenantId == tenant.Id && e.CreatedAt >= dayAgo);
            var recent = db.Deliveries.Where(d => d.Event!.TenantId == tenant.Id && d.CreatedAt >= dayAgo);
            var totalDeliveries = await recent.CountAsync();
            var delivered = await recent.CountAsync(d => d.Status == DeliveryStatus.Delivered);
            var failed = await recent.CountAsync(d => d.Status == DeliveryStatus.Failed);

            var rate = totalDeliveries > 0 ? Math.Round((double)delivered / totalDeliveries, 4) : 1.0;
            return new MetricsOut(totalEvents, rate, await queue.DepthAsync(), failed);
        });

        app.MapGet("/metrics/chart", async (HttpContext ctx, AppDbContext db, TimeProvider time) =>
        {
            var tenant = await AuthenticateAsync(ctx, db);
            var dayAgo = time.GetUtcNow().UtcDateTime.AddHours(-24);

            var rows = await db.Deliveries
                .Where(d => d.Event!.TenantId == tenant.Id && d.CreatedAt >= dayAgo)
                .Select(d => new { d.CreatedAt, d.Status })
                .ToListAsync();

            var buckets = rows
                .GroupBy(r => new DateTime(r.CreatedAt.Year, r.CreatedAt.Month, r.CreatedAt.Day, r.CreatedAt.Hour, 0, 0, DateTimeKind.Utc))
                .ToDictionary(g => g.Key, g => (Delivered: g.Count(r => r.Status == DeliveryStatus.Delivered), Total: g.Count()));

            var start = new DateTime(dayAgo.Year, dayAgo.Month, dayAgo.Day, dayAgo.Hour, 0, 0, DateTimeKind.Utc);
            var data = new List<ChartPoint>();
            for (var i = 0; i < 24; i++)
            {
                var hour = start.AddHours(i);
                buckets.TryGetValue(hour, out var b);
                var rate = b.Total > 0 ? Math.Round((double)b.Delivered / b.Total, 4) : 1.0;
                data.Add(new ChartPoint(hour.ToString("HH:mm"), rate));
            }
            return data;
        });
    }
}
