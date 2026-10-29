using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WebhookApi.Services;

public record WebhookResult(bool Success, string? Error);

public interface IWebhookSender
{
    Task<WebhookResult> SendAsync(string url, byte[] body, IDictionary<string, string> headers, CancellationToken ct = default);
}

public class HttpWebhookSender : IWebhookSender
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _client;

    public HttpWebhookSender()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = (context, ct) => SafeConnect.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct),
        };
        _client = new HttpClient(handler) { Timeout = Timeout };
    }

    public async Task<WebhookResult> SendAsync(string url, byte[] body, IDictionary<string, string> headers, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);

            using var response = await _client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return new WebhookResult(true, null);
            return new WebhookResult(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebhookResult(false, $"Connection timeout after {(int)Timeout.TotalMilliseconds}ms");
        }
        catch (HttpRequestException ex)
        {
            return new WebhookResult(false, ex.InnerException?.Message ?? ex.Message);
        }
    }
}

/// <summary>
/// Resolves the host itself and refuses to connect to private or loopback addresses.
/// Checking at connect time (not only when the endpoint is registered) covers DNS records
/// that change after registration.
/// </summary>
public static class SafeConnect
{
    public static async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0)
            throw new HttpRequestException($"Could not resolve host {host}");
        if (addresses.Any(SsrfGuard.IsForbidden))
            throw new HttpRequestException("Blocked: target resolves to a private or local address");

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw new HttpRequestException($"Could not connect to {host}", last);
    }
}
