using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralRouting.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace CentralRouting.Router.EntityFramework
{
    /// <summary>
    /// Router store based on Entity Framework Core. Works with any relational provider.
    /// </summary>
    public class EntityFrameworkRouterStore : IRouterStore
    {
        const int RevisionRowId = 1;
        readonly DbContextOptions<RouterDbContext> options;

        public EntityFrameworkRouterStore(DbContextOptions<RouterDbContext> options)
        {
            this.options = options;
        }

        RouterDbContext CreateContext() => new RouterDbContext(options);

        public async Task Initialize(CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await db.SubscriptionRequests.AnyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                //The database existed before but does not have the router's tables yet
                var creator = db.GetService<IRelationalDatabaseCreator>();
                await creator.CreateTablesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<IReadOnlyList<EndpointRecord>> GetEndpoints(CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var entities = await db.Endpoints.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            return entities.Select(ToRecord).ToList();
        }

        public async Task<EndpointRecord> GetEndpoint(string name, CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var entity = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Name == name, cancellationToken).ConfigureAwait(false);
            return entity == null ? null : ToRecord(entity);
        }

        public async Task SaveEndpoint(EndpointRecord endpoint, CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var newVersion = Guid.NewGuid().ToString();
            var entity = new EndpointEntity { Name = endpoint.Name, Data = RoutingJson.Serialize(endpoint), Version = newVersion };
            Track(db, entity, endpoint.Version, e => e.Data, e => e.Version);

            await Save(db, $"Endpoint {endpoint.Name}", cancellationToken).ConfigureAwait(false);
            endpoint.Version = newVersion;
        }

        public async Task<IReadOnlyList<RouteRecord>> GetRoutes(CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var entities = await db.Routes.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            return entities.Select(ToRecord).ToList();
        }

        public async Task<RouteRecord> GetRoute(string messageType, CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var entity = await db.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.MessageType == messageType, cancellationToken).ConfigureAwait(false);
            return entity == null ? null : ToRecord(entity);
        }

        public async Task SaveRoute(RouteRecord route, CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var newVersion = Guid.NewGuid().ToString();
            var entity = new RouteEntity { MessageType = route.MessageType, Data = RoutingJson.Serialize(route), Version = newVersion };
            Track(db, entity, route.Version, e => e.Data, e => e.Version);

            await Save(db, $"Route {route.MessageType}", cancellationToken).ConfigureAwait(false);
            route.Version = newVersion;
        }

        public async Task DeleteRoute(RouteRecord route, CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var entity = new RouteEntity { MessageType = route.MessageType, Version = route.Version };
            db.Routes.Remove(entity);
            await Save(db, $"Route {route.MessageType}", cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<SiteIdentityRecord>> GetSiteIdentities(CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            return await db.SiteIdentities.AsNoTracking()
                .Select(i => new SiteIdentityRecord { SiteId = i.SiteId, Identity = i.Identity, Source = i.Source, FirstSeen = i.FirstSeen })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task AddSiteIdentity(SiteIdentityRecord identity, CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var exists = await db.SiteIdentities
                .AnyAsync(i => i.SiteId == identity.SiteId && i.Identity == identity.Identity, cancellationToken).ConfigureAwait(false);
            if (exists)
            {
                return;
            }

            db.SiteIdentities.Add(new SiteIdentityEntity
            {
                SiteId = identity.SiteId,
                Identity = identity.Identity,
                Source = identity.Source,
                FirstSeen = identity.FirstSeen
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                //Added concurrently by another router process
            }
        }

        public async Task<IReadOnlyList<PeerRecord>> GetPeers(CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var entities = await db.Peers.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            return entities.Select(e =>
            {
                var record = RoutingJson.Deserialize<PeerRecord>(e.Data);
                record.Version = e.Version;
                return record;
            }).ToList();
        }

        public async Task SavePeer(PeerRecord peer, CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var newVersion = Guid.NewGuid().ToString();
            var entity = new PeerEntity { Name = peer.Name, Data = RoutingJson.Serialize(peer), Version = newVersion };
            Track(db, entity, peer.Version, e => e.Data, e => e.Version);

            await Save(db, $"Peer {peer.Name}", cancellationToken).ConfigureAwait(false);
            peer.Version = newVersion;
        }

        public async Task<IReadOnlyList<SubscriptionRequestRecord>> GetSubscriptionRequests(CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var entities = await db.SubscriptionRequests.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            return entities.Select(e =>
            {
                var record = RoutingJson.Deserialize<SubscriptionRequestRecord>(e.Data);
                record.Version = e.Version;
                return record;
            }).ToList();
        }

        public async Task SaveSubscriptionRequest(SubscriptionRequestRecord request, CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var newVersion = Guid.NewGuid().ToString();
            var entity = new SubscriptionRequestEntity { Id = RequestId(request), Data = RoutingJson.Serialize(request), Version = newVersion };
            Track(db, entity, request.Version, e => e.Data, e => e.Version);

            await Save(db, $"Subscription request {request.Key}", cancellationToken).ConfigureAwait(false);
            request.Version = newVersion;
        }

        public async Task DeleteSubscriptionRequest(SubscriptionRequestRecord request, CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            db.SubscriptionRequests.Remove(new SubscriptionRequestEntity { Id = RequestId(request), Version = request.Version });
            await Save(db, $"Subscription request {request.Key}", cancellationToken).ConfigureAwait(false);
        }

        static string RequestId(SubscriptionRequestRecord request)
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Key)));
        }

        public async Task<long> GetRevision(CancellationToken cancellationToken = default)
        {
            await using var db = CreateContext();
            var revision = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(r => r.Id == RevisionRowId, cancellationToken).ConfigureAwait(false);
            return revision?.Value ?? 0;
        }

        public async Task<long> IncrementRevision(CancellationToken cancellationToken = default)
        {
            for (var attempt = 1; ; attempt++)
            {
                await using var db = CreateContext();
                var revision = await db.Revisions.SingleOrDefaultAsync(r => r.Id == RevisionRowId, cancellationToken).ConfigureAwait(false);
                if (revision == null)
                {
                    revision = new RevisionEntity { Id = RevisionRowId };
                    db.Revisions.Add(revision);
                }

                revision.Value++;
                revision.Version = Guid.NewGuid().ToString();

                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return revision.Value;
                }
                catch (DbUpdateException) when (attempt < 20)
                {
                    //Incremented concurrently by another router process
                }
            }
        }

        /// <summary>
        /// Inserts the entity when there is no expected version, otherwise updates it only if the stored version matches.
        /// </summary>
        static void Track<T>(RouterDbContext db, T entity, string expectedVersion,
            System.Linq.Expressions.Expression<Func<T, string>> data, System.Linq.Expressions.Expression<Func<T, string>> version)
            where T : class
        {
            if (expectedVersion == null)
            {
                db.Add(entity);
                return;
            }

            var entry = db.Attach(entity);
            entry.Property(version).OriginalValue = expectedVersion;
            entry.Property(version).IsModified = true;
            entry.Property(data).IsModified = true;
        }

        static async Task Save(RouterDbContext db, string description, CancellationToken cancellationToken)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException e)
            {
                throw new StoreConcurrencyException($"{description} has been modified.", e);
            }
            catch (DbUpdateException e)
            {
                //An insert that collided with a concurrent insert of the same key
                throw new StoreConcurrencyException($"{description} has been created concurrently.", e);
            }
        }

        static EndpointRecord ToRecord(EndpointEntity entity)
        {
            var record = RoutingJson.Deserialize<EndpointRecord>(entity.Data);
            record.Version = entity.Version;
            return record;
        }

        static RouteRecord ToRecord(RouteEntity entity)
        {
            var record = RoutingJson.Deserialize<RouteRecord>(entity.Data);
            record.Version = entity.Version;
            return record;
        }
    }
}
