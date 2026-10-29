using StackExchange.Redis;

namespace WebhookApi.Services;

public static class Settings
{
    /// <summary>
    /// Accepts a postgres:// or postgresql:// URL (optionally with a "+asyncpg" suffix and
    /// a ?sslmode query, like the Python version's DATABASE_URL) or a plain Npgsql connection string.
    /// </summary>
    public static string ToNpgsqlConnectionString(string value)
    {
        if (!value.Contains("://"))
            return value;

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        var normalized = "postgresql" + value[schemeEnd..];
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            throw new ArgumentException("DATABASE_URL is not a valid URL");

        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var port = uri.Port > 0 ? uri.Port : 5432;
        var database = uri.AbsolutePath.TrimStart('/');
        var cs = $"Host={uri.Host};Port={port};Database={database};Username={user};Password={password}";
        if (uri.Query.Contains("sslmode=require", StringComparison.OrdinalIgnoreCase))
            cs += ";SSL Mode=Require";
        return cs;
    }

    public static ConfigurationOptions ToRedisOptions(string value)
    {
        ConfigurationOptions options;
        if (value.StartsWith("redis://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(value);
            options = new ConfigurationOptions();
            options.EndPoints.Add(uri.Host, uri.Port > 0 ? uri.Port : 6379);
            options.Ssl = uri.Scheme == "rediss";
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                if (parts.Length == 2)
                {
                    if (parts[0].Length > 0) options.User = Uri.UnescapeDataString(parts[0]);
                    options.Password = Uri.UnescapeDataString(parts[1]);
                }
                else
                {
                    options.Password = Uri.UnescapeDataString(parts[0]);
                }
            }
        }
        else
        {
            options = ConfigurationOptions.Parse(value);
        }
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 5000;
        return options;
    }
}
