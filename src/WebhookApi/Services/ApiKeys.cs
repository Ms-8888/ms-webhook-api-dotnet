using System.Security.Cryptography;
using System.Text;

namespace WebhookApi.Services;

public static class ApiKeys
{
    public static string Hash(string rawKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

    /// <summary>64 hex characters, used for both API keys and endpoint signing secrets.</summary>
    public static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
