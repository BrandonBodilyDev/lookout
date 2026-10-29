using Microsoft.EntityFrameworkCore;

namespace Lookout.Api.Data;

public sealed class LookoutDb(DbContextOptions<LookoutDb> options) : DbContext(options)
{
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<CrewRecord> Crews => Set<CrewRecord>();
    public DbSet<SafetyZoneRecord> SafetyZones => Set<SafetyZoneRecord>();
    public DbSet<EscapeRouteRecord> EscapeRoutes => Set<EscapeRouteRecord>();
    public DbSet<AlertRecord> Alerts => Set<AlertRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<CrewFix> CrewFixes => Set<CrewFix>();
    public DbSet<PushSubscriptionRecord> PushSubscriptions => Set<PushSubscriptionRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("postgis");

        b.Entity<Incident>(e =>
        {
            e.ToTable("incidents");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.WindSource).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.Version).IsRowVersion();
        });

        b.Entity<CrewRecord>(e =>
        {
            e.ToTable("crews");
            e.HasKey(x => new { x.IncidentId, x.CrewId });
            e.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId);
        });

        b.Entity<SafetyZoneRecord>(e =>
        {
            e.ToTable("safety_zones");
            e.HasKey(x => new { x.IncidentId, x.ZoneId });
            e.Property(x => x.Area).HasColumnType("geometry(Polygon, 4326)");
            e.Property(x => x.Version).IsRowVersion();
            e.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId);
        });

        b.Entity<EscapeRouteRecord>(e =>
        {
            e.ToTable("escape_routes");
            e.HasKey(x => new { x.IncidentId, x.RouteId });
            e.Property(x => x.Path).HasColumnType("geometry(LineStringZ, 4326)");
            e.Property(x => x.Version).IsRowVersion();
            e.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId);
        });

        b.Entity<AlertRecord>(e =>
        {
            e.ToTable("alerts", t => t.HasCheckConstraint("ck_alerts_ack_has_actor",
                "\"State\" <> 'Acknowledged' OR (\"AcknowledgedAt\" IS NOT NULL AND \"AcknowledgedBy\" IS NOT NULL)"));
            e.Property(x => x.Level).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Text).HasMaxLength(270);
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => new { x.IncidentId, x.CrewId, x.State });
            e.HasIndex(x => new { x.IncidentId, x.CrewId, x.LinkSeq }).IsUnique();
            e.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            // Hypertables need the partitioning column in every unique index.
            e.HasKey(x => new { x.Id, x.Time });
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Type).HasMaxLength(48);
            e.Property(x => x.Actor).HasMaxLength(64);
            e.Property(x => x.Role).HasMaxLength(16);
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => new { x.IncidentId, x.Time });
        });

        b.Entity<PushSubscriptionRecord>(e =>
        {
            e.ToTable("push_subscriptions");
            e.Property(x => x.Endpoint).HasMaxLength(1024);
            e.Property(x => x.P256dh).HasMaxLength(128);
            e.Property(x => x.Auth).HasMaxLength(64);
            e.HasIndex(x => new { x.IncidentId, x.CrewId, x.Endpoint }).IsUnique();
            e.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId);
        });

        b.Entity<CrewFix>(e =>
        {
            e.ToTable("crew_fixes");
            e.HasKey(x => new { x.IncidentId, x.CrewId, x.Time });
            e.Property(x => x.Position).HasColumnType("geometry(Point, 4326)");
        });
    }
}
