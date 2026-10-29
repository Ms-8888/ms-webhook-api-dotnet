using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WebhookApi.Data;

// Used only by `dotnet ef migrations`; no database connection is made.
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=webhooks;Username=postgres;Password=postgres")
            .Options;
        return new AppDbContext(options);
    }
}
