using System.Security.Cryptography;
using System.Text;

namespace WebhookApi.Services;

public static class RequestSigner
{
    /// <summary>
    /// "sha256=" + hex(HMAC-SHA256(secret, "&lt;timestamp&gt;." + payload)).
    /// Same scheme as the Python version.
    /// </summary>
    public static string Sign(string secret, byte[] payload, string timestamp)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var message = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(message, 0);
        payload.CopyTo(message, prefix.Length);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
        return "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();
    }
}
