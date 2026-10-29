using System.Net;
using System.Net.Sockets;

namespace WebhookApi.Services;

public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default);
}

public class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) =>
        Dns.GetHostAddressesAsync(host, ct);
}

public static class SsrfGuard
{
    private static readonly (IPAddress Network, int Prefix)[] Blocked =
    {
        (IPAddress.Parse("0.0.0.0"), 8),
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("100.64.0.0"), 10),
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("169.254.0.0"), 16),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("224.0.0.0"), 4),
        (IPAddress.Parse("240.0.0.0"), 4),
        (IPAddress.Parse("::"), 128),
        (IPAddress.Parse("::1"), 128),
        (IPAddress.Parse("fc00::"), 7),
        (IPAddress.Parse("fe80::"), 10),
        (IPAddress.Parse("ff00::"), 8),
    };

    public static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(Normalize(address));

    /// <summary>True for loopback, private, link-local, unspecified, multicast and reserved addresses.</summary>
    public static bool IsForbidden(IPAddress address)
    {
        address = Normalize(address);
        foreach (var (network, prefix) in Blocked)
        {
            if (network.AddressFamily == address.AddressFamily && InNetwork(address, network, prefix))
                return true;
        }
        return false;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool InNetwork(IPAddress address, IPAddress network, int prefix)
    {
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++)
            if (a[i] != n[i]) return false;
        var rest = prefix % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[fullBytes] & mask) == (n[fullBytes] & mask);
    }

    /// <summary>
    /// Returns an error message if the URL is unsafe, otherwise null. Hostnames are
    /// resolved and every returned address is checked, so a name that points to a
    /// private address is rejected too.
    /// </summary>
    public static async Task<string?> ValidateUrlAsync(string url, IDnsResolver resolver, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "Invalid URL";

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return "URL must use http or https";

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
            return "Invalid URL: missing hostname";

        var bare = host.Trim('[', ']');

        if (bare.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || bare.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "URL must not point to localhost";

        if (IPAddress.TryParse(bare, out var literal))
            return CheckAddress(literal, "point to");

        IPAddress[] addresses;
        try
        {
            addresses = await resolver.ResolveAsync(bare, ct);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return "URL hostname could not be resolved";
        }

        if (addresses.Length == 0)
            return "URL hostname could not be resolved";

        foreach (var address in addresses)
        {
            var error = CheckAddress(address, "resolve to");
            if (error != null) return error;
        }
        return null;
    }

    private static string? CheckAddress(IPAddress address, string verb)
    {
        if (IsLoopback(address)) return "URL must not point to localhost";
        if (IsForbidden(address)) return $"URL must not {verb} a private IP address";
        return null;
    }
}
