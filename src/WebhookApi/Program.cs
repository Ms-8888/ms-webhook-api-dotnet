using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using WebhookApi.Api;
using WebhookApi.Data;
using WebhookApi.Services;
using WebhookApi.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DictionaryKeyPolicy = null;
});

builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    var url = sp.GetRequiredService<IConfiguration>()["DATABASE_URL"]
        ?? throw new InvalidOperationException("DATABASE_URL is not set");
    options.UseNpgsql(Settings.ToNpgsqlConnectionString(url));
});

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var url = sp.GetRequiredService<IConfiguration>()["REDIS_URL"] ?? "redis://localhost:6379";
    return ConnectionMultiplexer.Connect(Settings.ToRedisOptions(url));
});
builder.Services.AddSingleton<IQueueStore, RedisQueueStore>();
builder.Services.AddSingleton<IDnsResolver, SystemDnsResolver>();
builder.Services.AddSingleton<IWebhookSender, HttpWebhookSender>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DeliveryProcessor>();
builder.Services.AddScoped<RetryScanner>();
builder.Services.AddHostedService<DispatchWorker>();
builder.Services.AddHostedService<RetryWorker>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("https://ms-8888.github.io", "http://localhost:5173")
    .WithMethods("GET", "POST", "DELETE")
    .WithHeaders("X-API-Key", "Content-Type")));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsNpgsql())
        db.Database.Migrate();
    else
        db.Database.EnsureCreated();

    if (args.Contains("seed"))
    {
        var givenKey = Environment.GetEnvironmentVariable("SEED_API_KEY");
        var rawKey = string.IsNullOrWhiteSpace(givenKey) ? ApiKeys.GenerateToken() : givenKey.Trim();
        var keyHash = ApiKeys.Hash(rawKey);
        if (db.Tenants.Any(t => t.ApiKeyHash == keyHash))
        {
            Console.WriteLine("A tenant with this key already exists. Nothing changed.");
            return;
        }
        db.Tenants.Add(new Tenant { Name = "Demo Tenant", ApiKeyHash = keyHash });
        db.SaveChanges();
        Console.WriteLine("Demo tenant created.");
        if (string.IsNullOrWhiteSpace(givenKey))
        {
            Console.WriteLine($"API Key: {rawKey}");
            Console.WriteLine("Use this as the X-API-Key header in all requests.");
        }
        else
        {
            Console.WriteLine("Use the key you provided in SEED_API_KEY as the X-API-Key header.");
        }
        return;
    }
}

app.UseCors();

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (ApiException ex)
    {
        ctx.Response.Clear();
        ctx.Response.StatusCode = ex.StatusCode;
        foreach (var (name, value) in ex.Headers)
            ctx.Response.Headers[name] = value;
        await ctx.Response.WriteAsJsonAsync(new { detail = ex.Detail });
    }
    catch (Exception ex) when (!ctx.Response.HasStarted)
    {
        app.Logger.LogError(ex, "Unhandled error");
        ctx.Response.Clear();
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { detail = "Internal Server Error" });
    }
});

app.MapWebhookRoutes();

app.Run();

public partial class Program { }
