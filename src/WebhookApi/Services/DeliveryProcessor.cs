using System.Text;
using Microsoft.EntityFrameworkCore;
using WebhookApi.Data;

namespace WebhookApi.Services;

/// <summary>Attempts one delivery and records the outcome in the database.</summary>
public class DeliveryProcessor
{
    private readonly AppDbContext _db;
    private readonly IWebhookSender _sender;
    private readonly TimeProvider _time;

    public DeliveryProcessor(AppDbContext db, IWebhookSender sender, TimeProvider time)
    {
        _db = db;
        _sender = sender;
        _time = time;
    }

    public async Task ProcessAsync(int deliveryId, CancellationToken ct = default)
    {
        var delivery = await _db.Deliveries
            .Include(d => d.Event)
            .Include(d => d.Endpoint)
            .FirstOrDefaultAsync(d => d.Id == deliveryId, ct);

        if (delivery is null || delivery.Status != DeliveryStatus.Pending)
            return;

        var endpoint = delivery.Endpoint!;
        if (!endpoint.IsActive)
        {
            delivery.Status = DeliveryStatus.Failed;
            delivery.LastError = "Endpoint no longer active";
            delivery.NextRetryAt = null;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var payload = Encoding.UTF8.GetBytes(delivery.Event!.Payload);
        var now = _time.GetUtcNow();
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var headers = new Dictionary<string, string>
        {
            ["X-Webhook-Signature"] = RequestSigner.Sign(endpoint.Secret, payload, timestamp),
            ["X-Webhook-Timestamp"] = timestamp,
            ["X-Event-Type"] = delivery.Event.EventType,
        };

        delivery.AttemptCount += 1;
        var result = await _sender.SendAsync(endpoint.Url, payload, headers, ct);

        if (result.Success)
        {
            delivery.Status = DeliveryStatus.Delivered;
            delivery.DeliveredAt = _time.GetUtcNow().UtcDateTime;
            delivery.NextRetryAt = null;
        }
        else
        {
            delivery.LastError = $"{result.Error} — attempt {delivery.AttemptCount} of {RetryPolicy.MaxAttempts}";
            var delay = RetryPolicy.DelayAfterFailedAttempt(delivery.AttemptCount);
            if (delivery.AttemptCount >= RetryPolicy.MaxAttempts || delay is null)
            {
                delivery.Status = DeliveryStatus.Failed;
                delivery.NextRetryAt = null;
            }
            else
            {
                delivery.NextRetryAt = _time.GetUtcNow().UtcDateTime + delay.Value;
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Finds deliveries whose retry time has passed and puts them back on the queue.
/// The retry schedule lives in the deliveries table, so it survives a restart.
/// </summary>
public class RetryScanner
{
    // Pushed deliveries get a short lease so the next scan does not queue them twice.
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly IQueueStore _queue;
    private readonly TimeProvider _time;

    public RetryScanner(AppDbContext db, IQueueStore queue, TimeProvider time)
    {
        _db = db;
        _queue = queue;
        _time = time;
    }

    public async Task<int> RequeueDueAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var due = await _db.Deliveries
            .Where(d => d.Status == DeliveryStatus.Pending
                        && d.NextRetryAt != null && d.NextRetryAt <= now
                        && d.AttemptCount > 0 && d.AttemptCount < RetryPolicy.MaxAttempts)
            .ToListAsync(ct);

        if (due.Count == 0) return 0;

        foreach (var d in due)
            d.NextRetryAt = now + Lease;
        await _db.SaveChangesAsync(ct);

        await _queue.PushAsync(due.Select(d => d.Id));
        return due.Count;
    }
}
