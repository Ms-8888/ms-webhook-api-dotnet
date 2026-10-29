namespace WebhookApi.Data;

public class Tenant
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ApiKeyHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}

public class Endpoint
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Url { get; set; } = "";
    public string? Description { get; set; }
    public string Secret { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
}

public class Event
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string EventType { get; set; } = "";
    // Raw JSON text of the payload object (jsonb on PostgreSQL, TEXT on SQLite).
    public string Payload { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
    public List<Delivery> Deliveries { get; set; } = new();
}

public class Delivery
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public int EndpointId { get; set; }
    public string Status { get; set; } = DeliveryStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Event? Event { get; set; }
    public Endpoint? Endpoint { get; set; }
}

public static class DeliveryStatus
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
}
