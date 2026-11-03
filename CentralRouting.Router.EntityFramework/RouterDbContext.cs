using System;
using Microsoft.EntityFrameworkCore;

namespace CentralRouting.Router.EntityFramework
{
    /// <summary>
    /// Endpoints and routes are stored as JSON documents in a single column, keyed by name,
    /// with a version column for optimistic concurrency.
    /// </summary>
    public class RouterDbContext : DbContext
    {
        public RouterDbContext(DbContextOptions<RouterDbContext> options) : base(options)
        {
        }

        public DbSet<EndpointEntity> Endpoints { get; set; }
        public DbSet<RouteEntity> Routes { get; set; }
        public DbSet<SiteIdentityEntity> SiteIdentities { get; set; }
        public DbSet<RevisionEntity> Revisions { get; set; }
        public DbSet<PeerEntity> Peers { get; set; }
        public DbSet<SubscriptionRequestEntity> SubscriptionRequests { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EndpointEntity>(e =>
            {
                e.ToTable("CentralRouting_Endpoints");
                e.HasKey(x => x.Name);
                e.Property(x => x.Name).HasMaxLength(200);
                e.Property(x => x.Data).IsRequired();
                e.Property(x => x.Version).HasMaxLength(36).IsConcurrencyToken();
            });

            modelBuilder.Entity<RouteEntity>(e =>
            {
                e.ToTable("CentralRouting_Routes");
                e.HasKey(x => x.MessageType);
                e.Property(x => x.MessageType).HasMaxLength(400);
                e.Property(x => x.Data).IsRequired();
                e.Property(x => x.Version).HasMaxLength(36).IsConcurrencyToken();
            });

            modelBuilder.Entity<SiteIdentityEntity>(e =>
            {
                e.ToTable("CentralRouting_SiteIdentities");
                e.HasKey(x => new { x.SiteId, x.Identity });
                e.Property(x => x.SiteId).HasMaxLength(200);
                e.Property(x => x.Identity).HasMaxLength(250);
                e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            });

            modelBuilder.Entity<PeerEntity>(e =>
            {
                e.ToTable("CentralRouting_Peers");
                e.HasKey(x => x.Name);
                e.Property(x => x.Name).HasMaxLength(200);
                e.Property(x => x.Data).IsRequired();
                e.Property(x => x.Version).HasMaxLength(36).IsConcurrencyToken();
            });

            modelBuilder.Entity<SubscriptionRequestEntity>(e =>
            {
                e.ToTable("CentralRouting_SubscriptionRequests");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasMaxLength(64);
                e.Property(x => x.Data).IsRequired();
                e.Property(x => x.Version).HasMaxLength(36).IsConcurrencyToken();
            });

            modelBuilder.Entity<RevisionEntity>(e =>
            {
                e.ToTable("CentralRouting_Revisions");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedNever();
                e.Property(x => x.Version).HasMaxLength(36).IsConcurrencyToken();
            });
        }
    }

    public class EndpointEntity
    {
        public string Name { get; set; }
        public string Data { get; set; }
        public string Version { get; set; }
    }

    public class RouteEntity
    {
        public string MessageType { get; set; }
        public string Data { get; set; }
        public string Version { get; set; }
    }

    public class SiteIdentityEntity
    {
        public string SiteId { get; set; }
        public string Identity { get; set; }
        public IdentitySource Source { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
    }

    public class PeerEntity
    {
        public string Name { get; set; }
        public string Data { get; set; }
        public string Version { get; set; }
    }

    /// <summary>
    /// Keyed by a hash of direction, peer and message type, which together can exceed index key limits.
    /// </summary>
    public class SubscriptionRequestEntity
    {
        public string Id { get; set; }
        public string Data { get; set; }
        public string Version { get; set; }
    }

    public class RevisionEntity
    {
        public int Id { get; set; }
        public long Value { get; set; }
        public string Version { get; set; }
    }
}
