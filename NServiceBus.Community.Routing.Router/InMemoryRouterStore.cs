using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Model;

namespace NServiceBus.Community.Routing.Router
{
    /// <summary>
    /// Non-durable store for tests and samples. Records are copied on the way in and out, like a real store.
    /// </summary>
    public class InMemoryRouterStore : IRouterStore
    {
        readonly object sync = new object();
        readonly Dictionary<string, EndpointRecord> endpoints = new Dictionary<string, EndpointRecord>();
        readonly Dictionary<string, RouteRecord> routes = new Dictionary<string, RouteRecord>();
        readonly List<SiteIdentityRecord> identities = new List<SiteIdentityRecord>();
        readonly Dictionary<string, PeerRecord> peers = new Dictionary<string, PeerRecord>();
        readonly Dictionary<string, SubscriptionRequestRecord> requests = new Dictionary<string, SubscriptionRequestRecord>();
        long revision;

        public Task Initialize(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<EndpointRecord>> GetEndpoints(CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                return Task.FromResult<IReadOnlyList<EndpointRecord>>(endpoints.Values.Select(Copy).ToList());
            }
        }

        public Task<EndpointRecord> GetEndpoint(string name, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                return Task.FromResult(endpoints.TryGetValue(name, out var e) ? Copy(e) : null);
            }
        }

        public Task SaveEndpoint(EndpointRecord endpoint, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                Save(endpoints, endpoint.Name, endpoint.Version, e => e.Version, endpoint, v => endpoint.Version = v);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RouteRecord>> GetRoutes(CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                return Task.FromResult<IReadOnlyList<RouteRecord>>(routes.Values.Select(Copy).ToList());
            }
        }

        public Task<RouteRecord> GetRoute(string messageType, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                return Task.FromResult(routes.TryGetValue(messageType, out var r) ? Copy(r) : null);
            }
        }

        public Task SaveRoute(RouteRecord route, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                Save(routes, route.MessageType, route.Version, r => r.Version, route, v => route.Version = v);
            }
            return Task.CompletedTask;
        }

        public Task DeleteRoute(RouteRecord route, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                if (!routes.TryGetValue(route.MessageType, out var stored) || stored.Version != route.Version)
                {
                    throw new StoreConcurrencyException($"Route for {route.MessageType} has been modified.");
                }
                routes.Remove(route.MessageType);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SiteIdentityRecord>> GetSiteIdentities(CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                return Task.FromResult<IReadOnlyList<SiteIdentityRecord>>(identities.Select(Copy).ToList());
            }
        }

        public Task AddSiteIdentity(SiteIdentityRecord identity, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                if (!identities.Any(i => i.SiteId == identity.SiteId && i.Identity == identity.Identity))
                {
                    identities.Add(Copy(identity));
                }
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PeerRecord>> GetPeers(CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                return Task.FromResult<IReadOnlyList<PeerRecord>>(peers.Values.Select(Copy).ToList());
            }
        }

        public Task SavePeer(PeerRecord peer, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                Save(peers, peer.Name, peer.Version, p => p.Version, peer, v => peer.Version = v);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SubscriptionRequestRecord>> GetSubscriptionRequests(CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                return Task.FromResult<IReadOnlyList<SubscriptionRequestRecord>>(requests.Values.Select(Copy).ToList());
            }
        }

        public Task SaveSubscriptionRequest(SubscriptionRequestRecord request, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                Save(requests, request.Key, request.Version, r => r.Version, request, v => request.Version = v);
            }
            return Task.CompletedTask;
        }

        public Task DeleteSubscriptionRequest(SubscriptionRequestRecord request, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                if (!requests.TryGetValue(request.Key, out var stored) || stored.Version != request.Version)
                {
                    throw new StoreConcurrencyException($"Subscription request {request.Key} has been modified.");
                }
                requests.Remove(request.Key);
            }
            return Task.CompletedTask;
        }

        public Task<long> GetRevision(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Interlocked.Read(ref revision));
        }

        public Task<long> IncrementRevision(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Interlocked.Increment(ref revision));
        }

        static void Save<T>(Dictionary<string, T> items, string key, string expectedVersion, Func<T, string> getVersion, T item, Action<string> setVersion)
            where T : class
        {
            var storedVersion = items.TryGetValue(key, out var stored) ? getVersion(stored) : null;
            if (storedVersion != expectedVersion)
            {
                throw new StoreConcurrencyException($"{typeof(T).Name} {key} has been modified.");
            }

            setVersion(Guid.NewGuid().ToString());
            items[key] = Copy(item);
        }

        static T Copy<T>(T item) => RoutingJson.Deserialize<T>(RoutingJson.Serialize(item));
    }
}
