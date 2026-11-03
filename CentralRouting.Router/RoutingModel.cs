using System;
using System.Collections.Generic;
using System.Linq;
using CentralRouting.Model;

namespace CentralRouting.Router
{
    /// <summary>
    /// A site the router is attached to and the transport address of the router's queue on it.
    /// </summary>
    public record RouterSite(string SiteId, string InterfaceName, IReadOnlyList<string> ConfiguredIdentities, string RouterAddress);

    /// <summary>
    /// The way to a site this router is not attached to: through a peer, on a site both routers share.
    /// </summary>
    public record PeerRoute(string Peer, string SharedSite, string PeerAddress);

    public enum InstanceStatus
    {
        Live,
        Stale,
        Stopped
    }

    public enum CapabilityState
    {
        Capable,
        Partial,
        Missing,
        Offline
    }

    public enum ValidationStatus
    {
        Valid,
        Warning,
        Invalid
    }

    public record ValidationFinding(string Code, ValidationStatus Severity, string Message);

    public class RouteValidation
    {
        public ValidationStatus Status { get; set; }
        public List<ValidationFinding> Findings { get; set; } = new List<ValidationFinding>();
    }

    /// <summary>
    /// Everything the router knows at one point in time: its own endpoints' reports, routes, peer snapshots and
    /// subscription requests. Derives capabilities, published addresses, validation results, the routing document
    /// and the snapshot for peers.
    /// </summary>
    public class RoutingModel
    {
        readonly DateTimeOffset now;
        readonly TimeSpan instanceExpiry;
        readonly TimeSpan peerExpiry;
        readonly Dictionary<string, HashSet<string>> baseTypes = new Dictionary<string, HashSet<string>>();
        readonly Dictionary<string, MessageKind> kinds = new Dictionary<string, MessageKind>();
        readonly Dictionary<string, List<(PeerRecord Peer, PeerEndpoint Endpoint)>> learned = new Dictionary<string, List<(PeerRecord, PeerEndpoint)>>();

        public RoutingModel(string routerName, IReadOnlyList<RouterSite> sites, IReadOnlyList<EndpointRecord> endpoints,
            IReadOnlyList<RouteRecord> routes, IReadOnlyList<SiteIdentityRecord> identities, DateTimeOffset now, TimeSpan instanceExpiry,
            IReadOnlyList<PeerRecord> peers = null, IReadOnlyList<SubscriptionRequestRecord> requests = null, TimeSpan? peerExpiry = null)
        {
            RouterName = routerName;
            this.now = now;
            this.instanceExpiry = instanceExpiry;
            this.peerExpiry = peerExpiry ?? TimeSpan.FromMinutes(15);
            Sites = sites;
            Identities = identities;
            Endpoints = endpoints.ToDictionary(e => e.Name);
            Routes = routes.ToDictionary(r => r.MessageType);
            Peers = (peers ?? Array.Empty<PeerRecord>()).Where(p => p.Snapshot != null).ToDictionary(p => p.Name);
            Requests = requests ?? Array.Empty<SubscriptionRequestRecord>();

            foreach (var handledType in endpoints.SelectMany(e => e.Instances).SelectMany(i => i.HandledTypes))
            {
                kinds[handledType.Type] = handledType.Kind;
                if (!baseTypes.TryGetValue(handledType.Type, out var bases))
                {
                    bases = new HashSet<string>();
                    baseTypes[handledType.Type] = bases;
                }
                bases.UnionWith(handledType.BaseTypes);
            }

            foreach (var publishedType in endpoints.SelectMany(e => e.Instances).SelectMany(i => i.PublishedTypes))
            {
                kinds[publishedType.Type] = MessageKind.Event;
                if (!baseTypes.TryGetValue(publishedType.Type, out var bases))
                {
                    bases = new HashSet<string>();
                    baseTypes[publishedType.Type] = bases;
                }
                bases.UnionWith(publishedType.BaseTypes);
            }

            foreach (var peer in Peers.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                foreach (var endpoint in peer.Snapshot.Endpoints)
                {
                    if (!learned.TryGetValue(endpoint.Name, out var sources))
                    {
                        sources = new List<(PeerRecord, PeerEndpoint)>();
                        learned[endpoint.Name] = sources;
                    }
                    sources.Add((peer, endpoint));
                }
            }
        }

        public string RouterName { get; }
        public IReadOnlyList<RouterSite> Sites { get; }
        public IReadOnlyList<SiteIdentityRecord> Identities { get; }

        /// <summary>
        /// Endpoint records stored by this router: endpoints it owns, and policy rows for learned endpoints.
        /// </summary>
        public IReadOnlyDictionary<string, EndpointRecord> Endpoints { get; }

        public IReadOnlyDictionary<string, RouteRecord> Routes { get; }
        public IReadOnlyDictionary<string, PeerRecord> Peers { get; }
        public IReadOnlyList<SubscriptionRequestRecord> Requests { get; }

        public bool IsOwned(string endpointName) => Endpoints.TryGetValue(endpointName, out var e) && e.Instances.Count > 0;

        public bool IsLearned(string endpointName) => learned.ContainsKey(endpointName);

        public IEnumerable<string> EndpointNames => Endpoints.Values.Where(e => e.Instances.Count > 0).Select(e => e.Name)
            .Union(learned.Keys)
            .OrderBy(n => n, StringComparer.Ordinal);

        /// <summary>
        /// The peers that own an endpoint which has never reported to this router. Such an endpoint receives events only
        /// through a subscription on its own router: this router forwards events to peers, not to their endpoints.
        /// </summary>
        public IReadOnlyList<string> GetOwningPeers(string endpointName)
        {
            return IsOwned(endpointName)
                ? Array.Empty<string>()
                : GetLearned(endpointName).Select(l => l.Peer.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        public static string PeerSubscriberMessage(string messageType, string endpointName, IReadOnlyList<string> peers)
        {
            var routers = string.Join(" or ", peers);
            return $"'{endpointName}' belongs to {routers}, so it can only subscribe to {messageType} there. " +
                   $"Request the event on {routers} and add the subscriber there.";
        }

        /// <summary>
        /// Each site of an Explicit endpoint is a separate business destination, such as one branch. Events between such
        /// sites are not supported: the sender addresses one site with a command instead.
        /// </summary>
        public static string ExplicitSubscriberMessage(string endpointName)
        {
            return $"'{endpointName}' uses the Explicit address policy, so it can't subscribe to events. Send it a command with RouteToSite instead.";
        }

        /// <summary>
        /// Events this router's routes subscribe the endpoint to.
        /// </summary>
        public IReadOnlyList<string> GetSubscriptions(string endpointName)
        {
            return Routes.Values
                .Where(r => r.Kind == RouteKind.Event && r.Subscribers.Contains(endpointName))
                .Select(r => r.MessageType)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();
        }

        public IReadOnlyList<(PeerRecord Peer, PeerEndpoint Endpoint)> GetLearned(string endpointName)
        {
            return learned.TryGetValue(endpointName, out var sources) ? sources : (IReadOnlyList<(PeerRecord, PeerEndpoint)>)Array.Empty<(PeerRecord, PeerEndpoint)>();
        }

        public bool IsPeerOnline(PeerRecord peer) => now - peer.LastReceivedAt <= peerExpiry;

        public InstanceStatus GetStatus(InstanceRecord instance)
        {
            if (instance.Stopped)
            {
                return InstanceStatus.Stopped;
            }
            return now - instance.LastReportAt > instanceExpiry ? InstanceStatus.Stale : InstanceStatus.Live;
        }

        /// <summary>
        /// Instances that receive traffic: live, not disabled and on a site where the endpoint is not disabled.
        /// </summary>
        public IEnumerable<InstanceRecord> RoutableInstances(EndpointRecord endpoint)
        {
            var disabledSites = endpoint.Sites.Where(s => s.Disabled).Select(s => s.SiteId).ToHashSet();
            return endpoint.Instances.Where(i => !i.Disabled && !disabledSites.Contains(i.SiteId) && GetStatus(i) == InstanceStatus.Live);
        }

        /// <summary>
        /// Capability of an owned endpoint for a message type on one site: the intersection over its routable instances there.
        /// </summary>
        public CapabilityState GetCapability(EndpointRecord endpoint, string site, string messageType)
        {
            var instances = RoutableInstances(endpoint).Where(i => i.SiteId == site).ToList();
            if (instances.Count == 0)
            {
                return CapabilityState.Offline;
            }

            var hierarchy = TypeAndBases(messageType);
            var capable = instances.Count(i => i.HandledTypes.Any(t => hierarchy.Contains(t.Type)));
            if (capable == instances.Count)
            {
                return CapabilityState.Capable;
            }
            return capable == 0 ? CapabilityState.Missing : CapabilityState.Partial;
        }

        public IReadOnlyDictionary<string, CapabilityState> GetCapabilities(EndpointRecord endpoint, string site)
        {
            return endpoint.Instances
                .Where(i => i.SiteId == site)
                .SelectMany(i => i.HandledTypes)
                .Select(t => t.Type)
                .Distinct()
                .ToDictionary(t => t, t => GetCapability(endpoint, site, t));
        }

        /// <summary>
        /// Capability of any endpoint, owned or learned, for a message type, per site it lives on.
        /// </summary>
        public Dictionary<string, CapabilityState> GetSiteCapabilities(string endpointName, string messageType)
        {
            var result = new Dictionary<string, CapabilityState>();
            if (Endpoints.TryGetValue(endpointName, out var owned))
            {
                foreach (var site in RoutableInstances(owned).Select(i => i.SiteId).Distinct())
                {
                    result[site] = GetCapability(owned, site, messageType);
                }
            }

            var hierarchy = TypeAndBases(messageType);
            foreach (var (_, endpoint) in GetLearned(endpointName))
            {
                foreach (var site in endpoint.Addresses.Where(a => a.Value.Values.Sum() > 0).Select(a => a.Key))
                {
                    endpoint.Capabilities.TryGetValue(site, out var capabilities);
                    var states = hierarchy
                        .Select(t => capabilities != null && capabilities.TryGetValue(t, out var state) ? state : CapabilityState.Missing)
                        .ToList();
                    result[site] = states.Contains(CapabilityState.Capable) ? CapabilityState.Capable
                        : states.Contains(CapabilityState.Partial) ? CapabilityState.Partial
                        : CapabilityState.Missing;
                }
            }
            return result;
        }

        /// <summary>
        /// Message types the router knows about, from reports, peers and routes.
        /// </summary>
        public IReadOnlyDictionary<string, MessageKind?> GetKnownTypes()
        {
            var result = new SortedDictionary<string, MessageKind?>(StringComparer.Ordinal);
            foreach (var kind in kinds)
            {
                result[kind.Key] = kind.Value;
            }
            foreach (var type in learned.Values.SelectMany(s => s).SelectMany(s => s.Endpoint.Capabilities.Values).SelectMany(c => c.Keys))
            {
                result.TryAdd(type, null);
            }
            foreach (var published in Peers.Values.SelectMany(p => p.Snapshot.PublishedEvents))
            {
                result.TryAdd(published.MessageType, MessageKind.Event);
            }
            foreach (var route in Routes.Values.Where(r => !result.ContainsKey(r.MessageType)))
            {
                result[route.MessageType] = route.Kind == RouteKind.Command ? MessageKind.Command : MessageKind.Event;
            }
            return result;
        }

        public IEnumerable<string> RoutedTypes(string endpointName)
        {
            return Routes.Values
                .Where(r => r.Destination == endpointName || r.Subscribers.Contains(endpointName))
                .Select(r => r.MessageType);
        }

        /// <summary>
        /// Address policy: a local record wins, otherwise the policy of the router that owns the endpoint.
        /// </summary>
        public AddressPolicy GetPolicy(string endpointName)
        {
            if (Endpoints.TryGetValue(endpointName, out var record))
            {
                return record.AddressPolicy;
            }
            return GetLearned(endpointName).Select(l => l.Endpoint.Policy).FirstOrDefault();
        }

        /// <summary>
        /// Sites an owned endpoint lives on, mapped to its queues and the number of routable instances behind each.
        /// </summary>
        public Dictionary<string, Dictionary<string, int>> GetPublishedAddresses(EndpointRecord endpoint)
        {
            var instances = RoutableInstances(endpoint).ToList();
            if (instances.Count == 0)
            {
                //An endpoint scaled to zero or restarting keeps its last known queues, so messages wait for it instead of being lost
                var disabledSites = endpoint.Sites.Where(s => s.Disabled).Select(s => s.SiteId).ToHashSet();
                instances = endpoint.Instances.Where(i => !i.Disabled && !disabledSites.Contains(i.SiteId)).ToList();
            }

            var addresses = instances
                .GroupBy(i => i.SiteId)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(i => i.InputQueue).ToDictionary(q => q.Key, q => q.Count()));

            if (endpoint.AddressPolicy == AddressPolicy.NewestSite && addresses.Count > 1)
            {
                var selected = SelectNewestSite(endpoint, addresses.Keys);
                addresses = addresses.Where(a => a.Key == selected).ToDictionary(a => a.Key, a => a.Value);
            }
            return addresses;
        }

        /// <summary>
        /// Addresses of an owned or learned endpoint. Each owner publishes its own sites.
        /// </summary>
        public Dictionary<string, Dictionary<string, int>> GetAddresses(string endpointName)
        {
            var result = Endpoints.TryGetValue(endpointName, out var owned)
                ? GetPublishedAddresses(owned)
                : new Dictionary<string, Dictionary<string, int>>();

            foreach (var (_, endpoint) in GetLearned(endpointName))
            {
                foreach (var site in endpoint.Addresses)
                {
                    if (!result.TryGetValue(site.Key, out var queues))
                    {
                        queues = new Dictionary<string, int>();
                        result[site.Key] = queues;
                    }
                    foreach (var queue in site.Value)
                    {
                        queues[queue.Key] = queues.GetValueOrDefault(queue.Key) + queue.Value;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// The newest site that is ready: it has a routable instance and covers every type routed to the endpoint.
        /// Falls back to the newest site with routable instances when none is ready.
        /// </summary>
        public string SelectNewestSite(EndpointRecord endpoint, IEnumerable<string> sitesWithInstances)
        {
            var routedTypes = RoutedTypes(endpoint.Name).ToList();
            var candidates = endpoint.Sites
                .Where(s => sitesWithInstances.Contains(s.SiteId))
                .OrderByDescending(s => s.FirstSeen)
                .ToList();

            var ready = candidates.FirstOrDefault(s => routedTypes.All(t => GetCapability(endpoint, s.SiteId, t) == CapabilityState.Capable));
            return (ready ?? candidates.First()).SiteId;
        }

        public bool IsAttached(string site) => Sites.Any(s => s.SiteId == site);

        /// <summary>
        /// Finds the peer attached to a site this router is not attached to, and a site both routers share.
        /// </summary>
        public PeerRoute FindPeerRoute(string site)
        {
            foreach (var peer in Peers.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (peer.Snapshot.Sites.All(s => s.SiteId != site))
                {
                    continue;
                }

                var shared = peer.Snapshot.Sites.FirstOrDefault(s => IsAttached(s.SiteId));
                if (shared != null)
                {
                    return new PeerRoute(peer.Name, shared.SiteId, shared.RouterAddress);
                }
            }
            return null;
        }

        /// <summary>
        /// Peers that hold a grant, or one being completed, for any of the given event types.
        /// </summary>
        public IEnumerable<string> GrantedPeers(IEnumerable<string> eventTypes)
        {
            var types = eventTypes.ToHashSet();
            return Requests
                .Where(r => r.Direction == SubscriptionDirection.Incoming && r.Status != SubscriptionRequestStatus.Requested && types.Contains(r.MessageType))
                .Select(r => r.Peer)
                .Distinct();
        }

        public bool HasGrant(string messageType)
        {
            return Requests.Any(r => r.Direction == SubscriptionDirection.Outgoing && r.Status == SubscriptionRequestStatus.Granted && r.MessageType == messageType);
        }

        /// <summary>
        /// Owned endpoints whose live instances declare that they publish the event.
        /// </summary>
        public IReadOnlyList<string> GetDeclaredPublishers(string messageType)
        {
            return Endpoints.Values
                .Where(e => e.Instances.Any(i => GetStatus(i) == InstanceStatus.Live && i.PublishedTypes.Any(t => t.Type == messageType)))
                .Select(e => e.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Events declared by exactly one endpoint that have no publisher yet. Declarations by several endpoints, by an endpoint
        /// when a peer publishes the event, or of a type routed as a command are left to the operator and reported by validation.
        /// </summary>
        public IReadOnlyList<(string MessageType, string Publisher)> GetPublisherAssignments()
        {
            return Endpoints.Values
                .SelectMany(e => e.Instances)
                .Where(i => GetStatus(i) == InstanceStatus.Live)
                .SelectMany(i => i.PublishedTypes)
                .Select(t => t.Type)
                .Distinct()
                .Where(type => !Routes.TryGetValue(type, out var route) || route.Kind == RouteKind.Event && route.Publisher == null)
                .Where(type => FindPublishingPeer(type) == null && !Requests.Any(r => r.Direction == SubscriptionDirection.Outgoing && r.MessageType == type))
                .Select(type => (type, publishers: GetDeclaredPublishers(type)))
                .Where(t => t.publishers.Count == 1)
                .Select(t => (t.type, t.publishers[0]))
                .OrderBy(t => t.Item1, StringComparer.Ordinal)
                .ToList();
        }

        public string FindPublishingPeer(string messageType)
        {
            return Peers.Values
                .Where(p => p.Snapshot.PublishedEvents.Any(e => e.MessageType == messageType))
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        public RoutingDocument BuildDocument(long revision)
        {
            var document = new RoutingDocument
            {
                Router = RouterName,
                Revision = revision,
                GeneratedAt = now
            };

            var peerSites = Peers.Values.SelectMany(p => p.Snapshot.Sites).Select(s => s.SiteId).Where(s => !IsAttached(s)).Distinct().ToList();

            foreach (var site in Sites)
            {
                var info = new SiteInfo { Router = site.RouterAddress };
                info.Identities.AddRange(site.ConfiguredIdentities);
                info.Identities.AddRange(Identities.Where(i => i.SiteId == site.SiteId).Select(i => i.Identity));
                info.Identities = info.Identities.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var other in Sites.Where(s => s.SiteId != site.SiteId))
                {
                    info.NextHop[other.SiteId] = site.RouterAddress;
                }

                foreach (var peerSite in peerSites)
                {
                    //A peer attached to this site and to the destination takes the message directly; otherwise this router forwards it
                    var direct = Peers.Values
                        .OrderBy(p => p.Name, StringComparer.Ordinal)
                        .Where(p => p.Snapshot.Sites.Any(s => s.SiteId == peerSite))
                        .Select(p => p.Snapshot.Sites.FirstOrDefault(s => s.SiteId == site.SiteId))
                        .FirstOrDefault(s => s != null);
                    info.NextHop[peerSite] = direct?.RouterAddress ?? site.RouterAddress;
                }
                document.Sites[site.SiteId] = info;
            }

            foreach (var name in EndpointNames)
            {
                document.Endpoints[name] = new EndpointInfo
                {
                    Policy = GetPolicy(name),
                    Addresses = GetAddresses(name)
                };
            }

            foreach (var route in Routes.Values)
            {
                if (route.Kind == RouteKind.Command)
                {
                    document.Routes[route.MessageType] = new CommandRoute { Endpoint = route.Destination };
                }
                else
                {
                    document.Events[route.MessageType] = new EventRoute
                    {
                        Publisher = route.Publisher,
                        PreviousPublisher = route.PreviousPublisher,
                        Subscribers = route.Subscribers.ToList(),
                        Forward = GrantedPeers(new[] { route.MessageType }).Any()
                    };
                }
            }
            return document;
        }

        /// <summary>
        /// What this router tells its peers: only what it owns.
        /// </summary>
        public PeerSnapshot BuildSnapshot(long revision)
        {
            return new PeerSnapshot
            {
                Router = RouterName,
                Revision = revision,
                SentAt = now,
                Sites = Sites.Select(s => new PeerSite
                {
                    SiteId = s.SiteId,
                    RouterAddress = s.RouterAddress,
                    Identities = s.ConfiguredIdentities.Union(Identities.Where(i => i.SiteId == s.SiteId).Select(i => i.Identity)).ToList()
                }).ToList(),
                Endpoints = Endpoints.Values
                    .Where(e => e.Instances.Count > 0)
                    .Select(e => new PeerEndpoint
                    {
                        Name = e.Name,
                        Policy = e.AddressPolicy,
                        Addresses = GetPublishedAddresses(e),
                        Capabilities = e.Sites.ToDictionary(s => s.SiteId, s => GetCapabilities(e, s.SiteId).ToDictionary(c => c.Key, c => c.Value))
                    }).ToList(),
                PublishedEvents = Routes.Values
                    .Where(r => r.Kind == RouteKind.Event && r.Publisher != null && IsOwned(r.Publisher))
                    .Select(r => new PublishedEvent { MessageType = r.MessageType, Publisher = r.Publisher })
                    .ToList(),
                Requests = Requests
                    .Where(r => r.Direction == SubscriptionDirection.Outgoing)
                    .Select(r => new PeerSubscription { MessageType = r.MessageType, Router = r.Peer })
                    .ToList(),
                Grants = Requests
                    .Where(r => r.Direction == SubscriptionDirection.Incoming && r.Status == SubscriptionRequestStatus.Granted)
                    .Select(r => new PeerSubscription { MessageType = r.MessageType, Router = r.Peer })
                    .ToList()
            };
        }

        public RouteValidation Validate(RouteRecord route)
        {
            var findings = new List<ValidationFinding>();
            if (route.Kind == RouteKind.Command)
            {
                ValidateDestination(route.MessageType, route.Destination, findings);
            }
            else
            {
                if (route.Publisher == null)
                {
                    if (!HasGrant(route.MessageType))
                    {
                        findings.Add(new ValidationFinding("NoPublisher", ValidationStatus.Warning,
                            "The event has no publisher assigned here and no peer granted a subscription request for it."));
                    }
                }
                else if (!IsOwned(route.Publisher))
                {
                    findings.Add(new ValidationFinding("UnknownPublisher", ValidationStatus.Warning,
                        $"Publisher '{route.Publisher}' has not reported to this router."));
                }
                else
                {
                    ValidateDeclaration(route.MessageType, route.Publisher, findings);
                }

                if (route.PreviousPublisher != null)
                {
                    ValidateMigration(route, findings);
                }

                ValidateOtherPublishers(route, findings);

                foreach (var subscriber in route.Subscribers)
                {
                    var owners = GetOwningPeers(subscriber);
                    if (owners.Count > 0)
                    {
                        findings.Add(new ValidationFinding("PeerSubscriber", ValidationStatus.Invalid, PeerSubscriberMessage(route.MessageType, subscriber, owners)));
                        continue;
                    }
                    if (GetPolicy(subscriber) == AddressPolicy.Explicit)
                    {
                        findings.Add(new ValidationFinding("ExplicitSubscriber", ValidationStatus.Invalid, ExplicitSubscriberMessage(subscriber)));
                        continue;
                    }
                    ValidateDestination(route.MessageType, subscriber, findings);
                }
            }

            return new RouteValidation
            {
                Findings = findings,
                Status = findings.Count == 0 ? ValidationStatus.Valid : findings.Max(f => f.Severity)
            };
        }

        /// <summary>
        /// Endpoints refuse to publish events they do not declare, so a publisher that does not declare the event publishes nothing.
        /// </summary>
        void ValidateDeclaration(string messageType, string publisher, List<ValidationFinding> findings)
        {
            var live = Endpoints[publisher].Instances.Where(i => GetStatus(i) == InstanceStatus.Live).ToList();
            if (live.Count == 0)
            {
                findings.Add(new ValidationFinding("PublisherOffline", ValidationStatus.Warning,
                    $"Publisher '{publisher}' has no live instances."));
                return;
            }

            var declaring = live.Count(i => i.PublishedTypes.Any(t => t.Type == messageType));
            if (declaring == 0)
            {
                findings.Add(new ValidationFinding("NotDeclared", ValidationStatus.Invalid,
                    $"'{publisher}' does not declare that it publishes {messageType}, so it cannot publish it."));
            }
            else if (declaring < live.Count)
            {
                findings.Add(new ValidationFinding("PartiallyDeclared", ValidationStatus.Warning,
                    $"Only some instances of '{publisher}' declare that they publish {messageType}."));
            }
        }

        /// <summary>
        /// A migration is meant to be temporary: report it until it is completed, and say when the old publisher is gone.
        /// </summary>
        void ValidateMigration(RouteRecord route, List<ValidationFinding> findings)
        {
            var previousLive = Endpoints.TryGetValue(route.PreviousPublisher, out var previous)
                               && previous.Instances.Any(i => GetStatus(i) == InstanceStatus.Live);
            var since = route.MigrationStartedAt is { } startedAt ? $" since {startedAt:u}" : "";
            findings.Add(new ValidationFinding("PublisherMigration", ValidationStatus.Warning, previousLive
                ? $"The publisher migrates from '{route.PreviousPublisher}' to '{route.Publisher}'{since}. Both may publish until the migration is completed."
                : $"The publisher migrates from '{route.PreviousPublisher}' to '{route.Publisher}'{since}. '{route.PreviousPublisher}' has no live instances; complete the migration."));
        }

        void ValidateOtherPublishers(RouteRecord route, List<ValidationFinding> findings)
        {
            var others = GetDeclaredPublishers(route.MessageType).Where(p => p != route.Publisher && p != route.PreviousPublisher).ToList();
            if (others.Count == 0)
            {
                return;
            }

            var peer = FindPublishingPeer(route.MessageType);
            var message = route.Publisher != null ? $"Only '{route.Publisher}' may publish it."
                : peer != null ? $"Peer {peer} publishes it."
                : "Assign one of them as the publisher.";
            if (route.Publisher != null && route.PreviousPublisher == null)
            {
                message += $" To move the event to {(others.Count == 1 ? $"'{others[0]}'" : "one of them")}, migrate the publisher.";
            }
            findings.Add(new ValidationFinding("PublisherConflict", ValidationStatus.Warning,
                $"{string.Join(", ", others.Select(o => $"'{o}'"))} also declare{(others.Count == 1 ? "s" : "")} that {(others.Count == 1 ? "it publishes" : "they publish")} {route.MessageType}. {message}"));
        }

        void ValidateDestination(string messageType, string endpointName, List<ValidationFinding> findings)
        {
            if (!IsOwned(endpointName) && !IsLearned(endpointName))
            {
                findings.Add(new ValidationFinding("UnknownEndpoint", ValidationStatus.Warning,
                    $"Endpoint '{endpointName}' has not reported to this router and no peer knows it."));
                return;
            }

            foreach (var (peer, _) in GetLearned(endpointName).Where(l => !IsPeerOnline(l.Peer)))
            {
                findings.Add(new ValidationFinding("PeerStale", ValidationStatus.Warning,
                    $"What is known about '{endpointName}' comes from peer {peer.Name}, which has not sent a snapshot since {peer.LastReceivedAt:u}."));
            }

            var capabilities = GetSiteCapabilities(endpointName, messageType);
            if (capabilities.Count == 0)
            {
                findings.Add(new ValidationFinding("Offline", ValidationStatus.Warning,
                    $"Endpoint '{endpointName}' has no live instances."));
                return;
            }

            if (capabilities.Values.All(c => c == CapabilityState.Missing))
            {
                findings.Add(new ValidationFinding("NotCapable", ValidationStatus.Invalid,
                    $"No instance of '{endpointName}' handles {messageType}."));
                return;
            }

            foreach (var capability in capabilities.Where(c => c.Value != CapabilityState.Capable).OrderBy(c => c.Key))
            {
                findings.Add(capability.Value == CapabilityState.Partial
                    ? new ValidationFinding("PartialCapability", ValidationStatus.Warning,
                        $"On site '{capability.Key}', only some instances of '{endpointName}' handle {messageType}.")
                    : new ValidationFinding("MissingOnSite", ValidationStatus.Warning,
                        $"On site '{capability.Key}', no instance of '{endpointName}' handles {messageType}."));
            }

            if (GetPolicy(endpointName) == AddressPolicy.Strict && capabilities.Count > 1)
            {
                findings.Add(new ValidationFinding("MultipleSites", ValidationStatus.Warning,
                    $"Endpoint '{endpointName}' lives on sites {string.Join(", ", capabilities.Keys.OrderBy(s => s))} but uses the Strict address policy."));
            }
        }

        HashSet<string> TypeAndBases(string messageType)
        {
            var result = new HashSet<string> { messageType };
            if (baseTypes.TryGetValue(messageType, out var bases))
            {
                result.UnionWith(bases);
            }
            return result;
        }
    }
}
