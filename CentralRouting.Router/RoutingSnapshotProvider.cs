using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralRouting.Model;

namespace CentralRouting.Router
{
    /// <summary>
    /// The routing state at one store revision, with the routing document generated from it.
    /// </summary>
    public class RoutingSnapshot
    {
        public RoutingSnapshot(long revision, RoutingModel model)
        {
            Revision = revision;
            Model = model;
            Document = model.BuildDocument(revision);
            Resolver = new AddressResolver(Document);
        }

        public long Revision { get; }
        public RoutingModel Model { get; }
        public RoutingDocument Document { get; }
        public AddressResolver Resolver { get; }
        public string ETag => $"\"{Revision}\"";
    }

    /// <summary>
    /// The sites the router is attached to, known once all interfaces are initialized.
    /// </summary>
    class RouterTopology
    {
        readonly Dictionary<string, RouterInterface> interfacesBySite = new Dictionary<string, RouterInterface>();

        public IReadOnlyList<RouterSite> Sites { get; private set; } = Array.Empty<RouterSite>();

        public void Initialize(IEnumerable<RouterInterface> interfaces)
        {
            foreach (var routerInterface in interfaces)
            {
                interfacesBySite[routerInterface.SiteId] = routerInterface;
            }

            Sites = interfacesBySite.Values
                .Select(i => new RouterSite(i.SiteId, i.Name, i.Configuration.SiteIdentities.ToList(), i.RouterAddress))
                .ToList();
        }

        public RouterInterface GetInterfaceByName(string name)
        {
            return interfacesBySite.Values.FirstOrDefault(i => i.Name == name)
                   ?? throw new InvalidOperationException($"The router has no interface named {name}.");
        }

        public RouterInterface GetInterface(string site)
        {
            if (site == null || !interfacesBySite.TryGetValue(site, out var routerInterface))
            {
                throw new UnforwardableMessageException($"The router is not attached to site '{site}'.");
            }
            return routerInterface;
        }
    }

    /// <summary>
    /// Keeps the current snapshot. Several router processes can share a store, so the snapshot is rebuilt
    /// whenever the stored revision changes.
    /// </summary>
    public class RoutingSnapshotProvider
    {
        readonly IRouterStore store;
        readonly RouterConfiguration configuration;
        readonly RouterTopology topology;
        readonly SemaphoreSlim refreshLock = new SemaphoreSlim(1, 1);
        volatile RoutingSnapshot current;

        internal RoutingSnapshotProvider(IRouterStore store, RouterConfiguration configuration, RouterTopology topology)
        {
            this.store = store;
            this.configuration = configuration;
            this.topology = topology;
        }

        public RoutingSnapshot Current => current ?? throw new InvalidOperationException("The router has not started yet.");

        public async Task<RoutingSnapshot> Refresh(bool force, CancellationToken cancellationToken)
        {
            await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var revision = await store.GetRevision(cancellationToken).ConfigureAwait(false);
                if (!force && current != null && current.Revision == revision)
                {
                    return current;
                }

                var endpoints = await store.GetEndpoints(cancellationToken).ConfigureAwait(false);
                var routes = await store.GetRoutes(cancellationToken).ConfigureAwait(false);
                var identities = await store.GetSiteIdentities(cancellationToken).ConfigureAwait(false);
                var peers = await store.GetPeers(cancellationToken).ConfigureAwait(false);
                var requests = await store.GetSubscriptionRequests(cancellationToken).ConfigureAwait(false);

                current = new RoutingSnapshot(revision, CreateModel(endpoints, routes, identities, peers, requests));
                return current;
            }
            finally
            {
                refreshLock.Release();
            }
        }

        /// <summary>
        /// Instances go stale without anyone writing to the store. When the set of routable instances changes,
        /// bump the revision so that endpoints and other router processes pick up the new addresses.
        /// Heartbeats are stored without bumping the revision, so the check reloads the state first: judged by the report
        /// times it was built with, a snapshot would see every instance go stale once it is older than the instance expiry.
        /// </summary>
        public async Task CheckLiveness(CancellationToken cancellationToken)
        {
            var snapshot = current;
            if (snapshot == null)
            {
                return;
            }

            var reloaded = await Refresh(true, cancellationToken).ConfigureAwait(false);
            if (!RoutableInstances(reloaded.Model).SetEquals(RoutableInstances(snapshot.Model)))
            {
                await Changed(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Called after this process changed the store.
        /// </summary>
        public async Task<RoutingSnapshot> Changed(CancellationToken cancellationToken)
        {
            await store.IncrementRevision(cancellationToken).ConfigureAwait(false);
            return await Refresh(true, cancellationToken).ConfigureAwait(false);
        }

        RoutingModel CreateModel(IReadOnlyList<EndpointRecord> endpoints, IReadOnlyList<RouteRecord> routes, IReadOnlyList<SiteIdentityRecord> identities,
            IReadOnlyList<PeerRecord> peers, IReadOnlyList<SubscriptionRequestRecord> requests)
        {
            return new RoutingModel(configuration.Name, topology.Sites, endpoints, routes, identities, DateTimeOffset.UtcNow, configuration.InstanceExpiry,
                peers, requests, configuration.PeerExpiry);
        }

        static HashSet<string> RoutableInstances(RoutingModel model)
        {
            return model.Endpoints.Values
                .SelectMany(e => model.RoutableInstances(e).Select(i => $"{e.Name}/{i.InstanceId}"))
                .ToHashSet();
        }
    }
}
