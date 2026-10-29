using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace WebhookApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Endpoint> Endpoints => Set<Endpoint>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // All timestamps are UTC. Providers that do not keep the Kind (SQLite) would
        // otherwise hand back Unspecified values that serialize without a "Z".
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
            e.Property(x => x.ApiKeyHash).HasColumnName("api_key_hash").HasMaxLength(64).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.HasIndex(x => x.ApiKeyHash).IsUnique();
        });

        b.Entity<Endpoint>(e =>
        {
            e.ToTable("endpoints");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Url).HasColumnName("url").HasMaxLength(2048).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(x => x.Secret).HasColumnName("secret").HasMaxLength(64).IsRequired();
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId);
        });

        b.Entity<Event>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(255).IsRequired();
            var payload = e.Property(x => x.Payload).HasColumnName("payload").IsRequired();
            payload.HasColumnType(Database.IsNpgsql() ? "jsonb" : "TEXT");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId);
        });

        b.Entity<Delivery>(e =>
        {
            e.ToTable("deliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.EndpointId).HasColumnName("endpoint_id");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            e.Property(x => x.NextRetryAt).HasColumnName("next_retry_at");
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.Property(x => x.DeliveredAt).HasColumnName("delivered_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne(x => x.Event).WithMany(x => x.Deliveries).HasForeignKey(x => x.EventId);
            e.HasOne(x => x.Endpoint).WithMany().HasForeignKey(x => x.EndpointId);
            e.HasIndex(x => x.EventId).HasDatabaseName("ix_deliveries_event_id");
            e.HasIndex(x => x.EndpointId).HasDatabaseName("ix_deliveries_endpoint_id");
            e.HasIndex(x => x.NextRetryAt).HasDatabaseName("ix_deliveries_next_retry_at");
            e.HasIndex(x => new { x.Status, x.NextRetryAt }).HasDatabaseName("ix_deliveries_status_retry");
        });
    }
}

public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
               v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
}

public class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
{
    public NullableUtcDateTimeConverter()
        : base(v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
               v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v) { }
}
