using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralRouting.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CentralRouting.Router
{
    /// <summary>
    /// The router's HTTP API, version 1.
    /// </summary>
    public static class RouterApi
    {
        /// <summary>
        /// Maps the router API under /api/v1.
        /// </summary>
        public static RouteGroupBuilder MapCentralRoutingApi(this IEndpointRouteBuilder app)
        {
            var api = app.MapGroup("/api/v1");

            api.MapGet("/routing-document", GetRoutingDocument);
            api.MapGet("/sites", GetSites);

            api.MapGet("/endpoints", GetEndpoints);
            api.MapGet("/endpoints/{name}", GetEndpoint);
            api.MapPut("/endpoints/{name}/address-policy", SetAddressPolicy);
            api.MapPut("/endpoints/{name}/instances/{instanceId}/disabled", SetInstanceDisabled);
            api.MapDelete("/endpoints/{name}/instances/{instanceId}", DeleteInstance);
            api.MapPut("/endpoints/{name}/sites/{site}/disabled", SetSiteDisabled);

            api.MapGet("/message-types", GetMessageTypes);
            api.MapGet("/message-types/{type}/candidates", GetCandidates);

            api.MapGet("/routes", GetRoutes);
            api.MapGet("/routes/{type}", GetRoute);
            api.MapPut("/routes/{type}/destination", SetDestination);
            api.MapPut("/routes/{type}/publisher", SetPublisher);
            api.MapPost("/routes/{type}/publisher/migration", StartPublisherMigration);
            api.MapDelete("/routes/{type}/publisher/migration", CompletePublisherMigration);
            api.MapPost("/routes/{type}/publisher/migration/rollback", RollBackPublisherMigration);
            api.MapPost("/routes/{type}/subscribers", AddSubscriber);
            api.MapDelete("/routes/{type}/subscribers/{endpoint}", RemoveSubscriber);
            api.MapDelete("/routes/{type}", DeleteRoute);
            api.MapPost("/routes/validate", ValidateRoute);

            api.MapPost("/peering/exchange", ExchangeSnapshots);
            api.MapGet("/peers", GetPeers);
            api.MapGet("/subscription-requests", GetSubscriptionRequests);
            api.MapPost("/subscription-requests", CreateSubscriptionRequest);
            api.MapDelete("/subscription-requests/{type}", DeleteSubscriptionRequest);

            return api;
        }

        static async Task<IResult> GetRoutingDocument(HttpContext http, RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var snapshot = await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false);
            if (http.Request.Headers.IfNoneMatch == snapshot.ETag)
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            http.Response.Headers.ETag = snapshot.ETag;
            return Results.Json(snapshot.Document, RoutingJson.Options);
        }

        static IResult GetSites(RoutingSnapshotProvider snapshots, SiteAvailability availability)
        {
            var document = snapshots.Current.Document;
            return Results.Ok(snapshots.Current.Model.Sites
                .Select(s => new SiteView(s.SiteId, s.InterfaceName, s.RouterAddress, document.Sites[s.SiteId].Identities, Status(s.SiteId))));

            SiteStatus Status(string site) =>
                !availability.IsConnected(site) ? SiteStatus.Connecting
                : availability.IsUnavailable(site) ? SiteStatus.Unreachable
                : SiteStatus.Connected;
        }

        static async Task<IResult> GetEndpoints(RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var snapshot = await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false);
            return Results.Ok(snapshot.Model.EndpointNames.Select(n => ToView(snapshot.Model, n)));
        }

        static async Task<IResult> GetEndpoint(string name, RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var snapshot = await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false);
            return snapshot.Model.EndpointNames.Contains(name)
                ? Results.Ok(ToView(snapshot.Model, name))
                : Results.NotFound();
        }

        /// <summary>
        /// Sets the policy of an owned endpoint, or of a learned one: senders on this router's sites use it instead of the owner's.
        /// </summary>
        static async Task<IResult> SetAddressPolicy(string name, SetAddressPolicyRequest request, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var model = (await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false)).Model;
            if (!model.EndpointNames.Contains(name))
            {
                return Results.NotFound();
            }

            var subscriptions = model.GetSubscriptions(name);
            if (request.Policy == AddressPolicy.Explicit && subscriptions.Count > 0)
            {
                return Results.Conflict($"{RoutingModel.ExplicitSubscriberMessage(name)} Remove its subscriptions first: {string.Join(", ", subscriptions)}.");
            }

            await store.UpdateEndpoint(name, true, endpoint =>
            {
                endpoint.AddressPolicy = request.Policy;
                return true;
            }, cancellationToken).ConfigureAwait(false);

            var snapshot = await snapshots.Changed(cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToView(snapshot.Model, name));
        }

        static Task<IResult> SetInstanceDisabled(string name, string instanceId, SetDisabledRequest request, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            return UpdateEndpoint(name, store, snapshots, endpoint =>
            {
                var instance = endpoint.Instances.FirstOrDefault(i => i.InstanceId == instanceId);
                if (instance == null)
                {
                    return false;
                }
                instance.Disabled = request.Disabled;
                return true;
            }, cancellationToken);
        }

        static Task<IResult> DeleteInstance(string name, string instanceId, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            return UpdateEndpoint(name, store, snapshots, endpoint => endpoint.Instances.RemoveAll(i => i.InstanceId == instanceId) > 0, cancellationToken);
        }

        static Task<IResult> SetSiteDisabled(string name, string site, SetDisabledRequest request, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            return UpdateEndpoint(name, store, snapshots, endpoint =>
            {
                var endpointSite = endpoint.Sites.FirstOrDefault(s => s.SiteId == site);
                if (endpointSite == null)
                {
                    return false;
                }
                endpointSite.Disabled = request.Disabled;
                return true;
            }, cancellationToken);
        }

        static async Task<IResult> GetMessageTypes(RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var snapshot = await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false);
            var model = snapshot.Model;
            return Results.Ok(model.GetKnownTypes().Select(t =>
            {
                model.Routes.TryGetValue(t.Key, out var route);
                return new MessageTypeView(t.Key, t.Value, route?.Kind, route == null ? null : model.Validate(route).Status, model.GetDeclaredPublishers(t.Key));
            }));
        }

        static async Task<IResult> GetCandidates(string type, RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var snapshot = await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false);
            var model = snapshot.Model;
            var candidates = model.EndpointNames
                .Select(n => new CandidateView(n, model.GetSiteCapabilities(n, type)))
                .Where(c => c.Sites.Values.Any(s => s is CapabilityState.Capable or CapabilityState.Partial))
                .OrderBy(c => c.Endpoint);
            return Results.Ok(candidates);
        }

        static async Task<IResult> GetRoutes(RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var snapshot = await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false);
            return Results.Ok(snapshot.Model.Routes.Values.OrderBy(r => r.MessageType).Select(r => ToView(snapshot.Model, r)));
        }

        static async Task<IResult> GetRoute(string type, RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var snapshot = await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false);
            return snapshot.Model.Routes.TryGetValue(type, out var route)
                ? Results.Ok(ToView(snapshot.Model, route))
                : Results.NotFound();
        }

        static Task<IResult> SetDestination(string type, SetEndpointRequest request, HttpContext http, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            return UpdateRoute(type, RouteKind.Command, http, store, snapshots, request.Comment, route =>
            {
                route.Destination = request.Endpoint;
                return true;
            }, cancellationToken);
        }

        static Task<IResult> SetPublisher(string type, SetEndpointRequest request, HttpContext http, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            return UpdateRoute(type, RouteKind.Event, http, store, snapshots, request.Comment, route =>
            {
                if (route.PreviousPublisher != null)
                {
                    throw new RouteConflictException($"The publisher of {type} is migrating from {route.PreviousPublisher} to {route.Publisher}. Complete or roll back the migration first.");
                }
                route.Publisher = request.Endpoint;
                return true;
            }, cancellationToken);
        }

        /// <summary>
        /// Makes another endpoint the publisher while the current one may still publish, e.g. while a replacement is deployed
        /// next to the endpoint it replaces. The migration lasts until it is completed or rolled back.
        /// </summary>
        static Task<IResult> StartPublisherMigration(string type, SetEndpointRequest request, HttpContext http, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            return UpdateRoute(type, RouteKind.Event, http, store, snapshots, request.Comment, route =>
            {
                if (route.Publisher == null)
                {
                    throw new RouteConflictException($"{type} has no publisher to migrate from. Assign one with PUT /api/v1/routes/{{type}}/publisher.");
                }
                if (route.PreviousPublisher != null)
                {
                    throw new RouteConflictException($"The publisher of {type} is already migrating from {route.PreviousPublisher} to {route.Publisher}.");
                }
                if (route.Publisher == request.Endpoint)
                {
                    throw new RouteConflictException($"{request.Endpoint} already is the publisher of {type}.");
                }
                route.PreviousPublisher = route.Publisher;
                route.Publisher = request.Endpoint;
                route.MigrationStartedAt = DateTimeOffset.UtcNow;
                return true;
            }, cancellationToken);
        }

        static Task<IResult> CompletePublisherMigration(string type, HttpContext http, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            return UpdateRoute(type, RouteKind.Event, http, store, snapshots, null, route =>
            {
                if (route.PreviousPublisher == null)
                {
                    throw new RouteConflictException($"The publisher of {type} is not migrating.");
                }
                route.PreviousPublisher = null;
                route.MigrationStartedAt = null;
                return true;
            }, cancellationToken);
        }

        static Task<IResult> RollBackPublisherMigration(string type, HttpContext http, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            return UpdateRoute(type, RouteKind.Event, http, store, snapshots, null, route =>
            {
                if (route.PreviousPublisher == null)
                {
                    throw new RouteConflictException($"The publisher of {type} is not migrating.");
                }
                route.Publisher = route.PreviousPublisher;
                route.PreviousPublisher = null;
                route.MigrationStartedAt = null;
                return true;
            }, cancellationToken);
        }

        /// <summary>
        /// A subscription belongs to the subscriber's router, and is possible only when that router knows the publisher is
        /// available: it has a publisher assigned here, or the router that owns the publisher granted a subscription request.
        /// </summary>
        static async Task<IResult> AddSubscriber(string type, SetEndpointRequest request, HttpContext http, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var model = (await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false)).Model;
            var owners = model.GetOwningPeers(request.Endpoint);
            if (owners.Count > 0)
            {
                return Results.Conflict(RoutingModel.PeerSubscriberMessage(type, request.Endpoint, owners));
            }
            if (model.GetPolicy(request.Endpoint) == AddressPolicy.Explicit)
            {
                return Results.Conflict(RoutingModel.ExplicitSubscriberMessage(request.Endpoint));
            }

            var hasLocalPublisher = model.Routes.TryGetValue(type, out var existing) && existing.Publisher != null;
            if (!hasLocalPublisher && !model.HasGrant(type))
            {
                return Results.Conflict($"No publisher of {type} is available. Assign one with PUT /api/v1/routes/{{type}}/publisher, " +
                                        "or request a subscription from the router that owns the publisher.");
            }

            return await UpdateRoute(type, RouteKind.Event, http, store, snapshots, request.Comment, route =>
            {
                if (route.Subscribers.Contains(request.Endpoint))
                {
                    return false;
                }
                route.Subscribers.Add(request.Endpoint);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        static Task<IResult> RemoveSubscriber(string type, string endpoint, HttpContext http, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            return UpdateRoute(type, RouteKind.Event, http, store, snapshots, null, route => route.Subscribers.Remove(endpoint), cancellationToken);
        }

        static async Task<IResult> DeleteRoute(string type, IRouterStore store, RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                var route = await store.GetRoute(type, cancellationToken).ConfigureAwait(false);
                if (route == null)
                {
                    return Results.NotFound();
                }

                try
                {
                    await store.DeleteRoute(route, cancellationToken).ConfigureAwait(false);
                    await snapshots.Changed(cancellationToken).ConfigureAwait(false);
                    return Results.NoContent();
                }
                catch (StoreConcurrencyException) when (attempt < 10)
                {
                }
            }
        }

        static IResult ValidateRoute(ProposedRoute proposed, RoutingSnapshotProvider snapshots)
        {
            var route = new RouteRecord
            {
                MessageType = proposed.MessageType,
                Kind = proposed.Kind,
                Destination = proposed.Destination,
                Publisher = proposed.Publisher,
                Subscribers = proposed.Subscribers ?? new List<string>()
            };
            return Results.Ok(snapshots.Current.Model.Validate(route));
        }

        static async Task<IResult> UpdateEndpoint(string name, IRouterStore store, RoutingSnapshotProvider snapshots,
            Func<EndpointRecord, bool> change, CancellationToken cancellationToken)
        {
            var found = false;
            await store.UpdateEndpoint(name, false, endpoint =>
            {
                found = true;
                return change(endpoint);
            }, cancellationToken).ConfigureAwait(false);

            if (!found)
            {
                return Results.NotFound();
            }

            var snapshot = await snapshots.Changed(cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToView(snapshot.Model, name));
        }

        static async Task<IResult> UpdateRoute(string type, RouteKind kind, HttpContext http, IRouterStore store,
            RoutingSnapshotProvider snapshots, string comment, Func<RouteRecord, bool> change, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                var route = await store.GetRoute(type, cancellationToken).ConfigureAwait(false)
                            ?? new RouteRecord { MessageType = type, Kind = kind };

                if (route.Kind != kind)
                {
                    return Results.Conflict($"{type} is routed as a {route.Kind.ToString().ToLowerInvariant()}.");
                }

                bool changed;
                try
                {
                    changed = change(route);
                }
                catch (RouteConflictException e)
                {
                    return Results.Conflict(e.Message);
                }

                if (!changed)
                {
                    var current = await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false);
                    return current.Model.Routes.TryGetValue(type, out var existing) ? Results.Ok(ToView(current.Model, existing)) : Results.NotFound();
                }

                route.ChangedBy = http.User.Identity?.Name ?? "anonymous";
                route.ChangedAt = DateTimeOffset.UtcNow;
                route.Comment = comment;

                try
                {
                    await store.SaveRoute(route, cancellationToken).ConfigureAwait(false);
                }
                catch (StoreConcurrencyException) when (attempt < 10)
                {
                    continue;
                }

                var snapshot = await snapshots.Changed(cancellationToken).ConfigureAwait(false);
                return Results.Ok(ToView(snapshot.Model, snapshot.Model.Routes[type]));
            }
        }

        static async Task<IResult> ExchangeSnapshots(PeerSnapshot snapshot, PeeringService peering, CancellationToken cancellationToken)
        {
            if (!peering.IsPeer(snapshot.Router))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return Results.Json(await peering.Exchange(snapshot, cancellationToken).ConfigureAwait(false), RoutingJson.Options);
        }

        static async Task<IResult> GetPeers(RouterConfiguration configuration, RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var model = (await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false)).Model;
            return Results.Ok(configuration.Peers.Select(p =>
            {
                if (!model.Peers.TryGetValue(p.Name, out var peer))
                {
                    return new PeerView(p.Name, p.Channel, PeerStatus.Unknown, 0, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<PublishedEvent>());
                }

                return new PeerView(p.Name, p.Channel, model.IsPeerOnline(peer) ? PeerStatus.Online : PeerStatus.Offline,
                    peer.Snapshot.Revision, peer.LastReceivedAt,
                    peer.Snapshot.Sites.Select(s => s.SiteId).ToList(),
                    peer.Snapshot.Endpoints.Select(e => e.Name).ToList(),
                    peer.Snapshot.PublishedEvents);
            }));
        }

        static async Task<IResult> GetSubscriptionRequests(RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var model = (await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false)).Model;
            return Results.Ok(model.Requests.OrderBy(r => r.Direction).ThenBy(r => r.MessageType).Select(ToView));
        }

        /// <summary>
        /// Asks the router that owns the publisher of an event to forward it here. Subscribing local endpoints
        /// becomes possible once that router grants the request.
        /// </summary>
        static async Task<IResult> CreateSubscriptionRequest(CreateSubscriptionRequest request, IRouterStore store,
            RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var model = (await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false)).Model;
            if (model.Routes.TryGetValue(request.MessageType, out var route) && route.Publisher != null)
            {
                return Results.Conflict($"{request.MessageType} is published by {route.Publisher} on this router. Add subscribers directly.");
            }

            var peer = model.FindPublishingPeer(request.MessageType);
            if (peer == null)
            {
                return Results.Conflict($"No peer publishes {request.MessageType}.");
            }

            var existing = model.Requests.FirstOrDefault(r => r.Direction == SubscriptionDirection.Outgoing && r.MessageType == request.MessageType);
            if (existing != null)
            {
                return Results.Ok(ToView(existing));
            }

            var record = new SubscriptionRequestRecord
            {
                MessageType = request.MessageType,
                Peer = peer,
                Direction = SubscriptionDirection.Outgoing,
                Status = SubscriptionRequestStatus.Requested,
                RequestedAt = DateTimeOffset.UtcNow
            };

            try
            {
                await store.SaveSubscriptionRequest(record, cancellationToken).ConfigureAwait(false);
            }
            catch (StoreConcurrencyException)
            {
                return Results.Conflict($"A request for {request.MessageType} was created concurrently.");
            }

            await snapshots.Changed(cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToView(record));
        }

        static async Task<IResult> DeleteSubscriptionRequest(string type, IRouterStore store, RoutingSnapshotProvider snapshots, CancellationToken cancellationToken)
        {
            var model = (await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false)).Model;
            var request = model.Requests.FirstOrDefault(r => r.Direction == SubscriptionDirection.Outgoing && r.MessageType == type);
            if (request == null)
            {
                return Results.NotFound();
            }

            if (model.Routes.TryGetValue(type, out var route) && route.Publisher == null && route.Subscribers.Count > 0)
            {
                return Results.Conflict($"Remove the subscribers of {type} first: {string.Join(", ", route.Subscribers)}.");
            }

            await store.DeleteSubscriptionRequest(request, cancellationToken).ConfigureAwait(false);
            await snapshots.Changed(cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }

        /// <summary>
        /// A route change that conflicts with the route's current state. Returned as 409.
        /// </summary>
        class RouteConflictException : Exception
        {
            public RouteConflictException(string message)
                : base(message)
            {
            }
        }

        static SubscriptionRequestView ToView(SubscriptionRequestRecord request)
        {
            return new SubscriptionRequestView(request.MessageType, request.Peer, request.Direction, request.Status, request.RequestedAt, request.GrantedAt);
        }

        static EndpointView ToView(RoutingModel model, string name)
        {
            model.Endpoints.TryGetValue(name, out var endpoint);
            var owned = model.IsOwned(name);
            var learned = model.GetLearned(name);
            var addresses = model.GetAddresses(name);

            var sites = new List<EndpointSiteView>();
            if (owned)
            {
                sites.AddRange(endpoint.Sites.Select(s => new EndpointSiteView(s.SiteId, s.FirstSeen, s.Disabled,
                    addresses.TryGetValue(s.SiteId, out var queues) ? queues : new Dictionary<string, int>(),
                    model.GetCapabilities(endpoint, s.SiteId))));
            }
            foreach (var (_, learnedEndpoint) in learned)
            {
                sites.AddRange(learnedEndpoint.Addresses.Keys
                    .Where(site => sites.All(s => s.SiteId != site))
                    .Select(site => new EndpointSiteView(site, default, false, learnedEndpoint.Addresses[site],
                        learnedEndpoint.Capabilities.TryGetValue(site, out var capabilities) ? capabilities : new Dictionary<string, CapabilityState>())));
            }

            var instances = (owned ? endpoint.Instances : new List<InstanceRecord>())
                .OrderBy(i => i.InstanceId)
                .Select(i => new InstanceView(i.InstanceId, i.SiteId, i.InputQueue, i.HostName, model.GetStatus(i), i.Disabled, i.LastReportAt, i.DocumentRevision))
                .ToList();

            var owner = owned ? model.RouterName : string.Join(", ", learned.Select(l => l.Peer.Name));
            return new EndpointView(name, owner, model.GetPolicy(name), sites.OrderBy(s => s.SiteId).ToList(), instances);
        }

        static RouteView ToView(RoutingModel model, RouteRecord route)
        {
            return new RouteView(route.MessageType, route.Kind, route.Destination, route.Publisher, route.PreviousPublisher, route.MigrationStartedAt, route.Subscribers,
                route.ChangedBy, route.ChangedAt, route.Comment, route.Version, model.Validate(route));
        }
    }
}
