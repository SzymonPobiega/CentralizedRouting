using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NServiceBus.Community.Routing.Router
{
    /// <summary>
    /// Persistence used by the router. Endpoints and routes are stored as whole documents with optimistic concurrency,
    /// so the store can be backed by a relational or a document database.
    /// </summary>
    public interface IRouterStore
    {
        Task Initialize(CancellationToken cancellationToken = default);

        Task<IReadOnlyList<EndpointRecord>> GetEndpoints(CancellationToken cancellationToken = default);
        Task<EndpointRecord> GetEndpoint(string name, CancellationToken cancellationToken = default);

        /// <summary>
        /// Inserts the endpoint when its version is null, otherwise updates it if the stored version matches.
        /// Assigns the new version. Throws <see cref="StoreConcurrencyException"/> on conflict.
        /// </summary>
        Task SaveEndpoint(EndpointRecord endpoint, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<RouteRecord>> GetRoutes(CancellationToken cancellationToken = default);
        Task<RouteRecord> GetRoute(string messageType, CancellationToken cancellationToken = default);

        /// <summary>
        /// Same semantics as <see cref="SaveEndpoint"/>.
        /// </summary>
        Task SaveRoute(RouteRecord route, CancellationToken cancellationToken = default);

        Task DeleteRoute(RouteRecord route, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<SiteIdentityRecord>> GetSiteIdentities(CancellationToken cancellationToken = default);

        /// <summary>
        /// Adds the identity unless it is already stored.
        /// </summary>
        Task AddSiteIdentity(SiteIdentityRecord identity, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<PeerRecord>> GetPeers(CancellationToken cancellationToken = default);

        /// <summary>
        /// Same semantics as <see cref="SaveEndpoint"/>.
        /// </summary>
        Task SavePeer(PeerRecord peer, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<SubscriptionRequestRecord>> GetSubscriptionRequests(CancellationToken cancellationToken = default);

        /// <summary>
        /// Same semantics as <see cref="SaveEndpoint"/>. Requests are identified by direction, peer and message type.
        /// </summary>
        Task SaveSubscriptionRequest(SubscriptionRequestRecord request, CancellationToken cancellationToken = default);

        Task DeleteSubscriptionRequest(SubscriptionRequestRecord request, CancellationToken cancellationToken = default);

        Task<long> GetRevision(CancellationToken cancellationToken = default);

        /// <summary>
        /// Increments the routing document revision and returns the new value.
        /// </summary>
        Task<long> IncrementRevision(CancellationToken cancellationToken = default);
    }

    public class StoreConcurrencyException : Exception
    {
        public StoreConcurrencyException(string message, Exception innerException = null) : base(message, innerException)
        {
        }
    }
}
