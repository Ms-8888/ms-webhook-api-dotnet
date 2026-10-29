using System.Text.Json;
using System.Text.Json.Serialization;
using WebhookApi.Data;
using Endpoint = WebhookApi.Data.Endpoint;

namespace WebhookApi.Api;

// Property names are serialized as snake_case (see Program.cs), matching the Python API.

public record EndpointOut(int Id, string Url, string? Description, bool IsActive, DateTime CreatedAt)
{
    public static EndpointOut From(Endpoint e) => new(e.Id, e.Url, e.Description, e.IsActive, e.CreatedAt);
}

public record EventOut(int Id, string EventType, JsonElement Payload, DateTime CreatedAt)
{
    public static EventOut From(Event e)
    {
        using var doc = JsonDocument.Parse(e.Payload);
        return new EventOut(e.Id, e.EventType, doc.RootElement.Clone(), e.CreatedAt);
    }
}

public record DeliveryOut(
    int Id, int EndpointId, string EndpointUrl, string Status, int AttemptCount,
    string? LastError, DateTime? DeliveredAt, DateTime CreatedAt);

public record MetricsOut(
    int TotalEventsToday,
    double DeliverySuccessRate,
    long QueueDepth,
    [property: JsonPropertyName("failed_deliveries_24h")] int FailedDeliveries24h);

public record HealthOut(string Status, string Db, string Redis);

public record FiredEventOut(int EventId, int QueuedDeliveries);

public record ChartPoint(string Hour, double Rate);
