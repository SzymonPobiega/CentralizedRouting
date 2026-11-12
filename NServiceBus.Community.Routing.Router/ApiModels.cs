using System;
using System.Collections.Generic;
using NServiceBus.Community.Routing.Model;

namespace NServiceBus.Community.Routing.Router
{
    public record SetAddressPolicyRequest(AddressPolicy Policy);

    public record SetDisabledRequest(bool Disabled);

    public record SetEndpointRequest(string Endpoint, string Comment = null);

    public record ProposedRoute(string MessageType, RouteKind Kind, string Destination, string Publisher, List<string> Subscribers);

    public record SiteView(string SiteId, string Interface, string RouterAddress, IReadOnlyList<string> Identities, SiteStatus Status);

    public enum SiteStatus
    {
        /// <summary>
        /// The interface is connected and the last dispatch to the site succeeded.
        /// </summary>
        Connected,

        /// <summary>
        /// The interface is connected, but the site did not accept the last dispatch or probe.
        /// </summary>
        Unreachable,

        /// <summary>
        /// The interface has not connected yet; the router keeps trying.
        /// </summary>
        Connecting
    }

    public record InstanceView(string InstanceId, string SiteId, string InputQueue, string HostName, InstanceStatus Status,
        bool Disabled, DateTimeOffset LastReportAt, long DocumentRevision);

    public record EndpointSiteView(string SiteId, DateTimeOffset FirstSeen, bool Disabled,
        IReadOnlyDictionary<string, int> Addresses, IReadOnlyDictionary<string, CapabilityState> Capabilities);

    public record EndpointView(string Name, string Owner, AddressPolicy AddressPolicy, IReadOnlyList<EndpointSiteView> Sites, IReadOnlyList<InstanceView> Instances);

    public enum PeerStatus
    {
        Unknown,
        Online,
        Offline
    }

    public record PeerView(string Name, PeerChannel Channel, PeerStatus Status, long Revision, DateTimeOffset? LastReceivedAt,
        IReadOnlyList<string> Sites, IReadOnlyList<string> Endpoints, IReadOnlyList<PublishedEvent> PublishedEvents);

    public record CreateSubscriptionRequest(string MessageType);

    public record SubscriptionRequestView(string MessageType, string Peer, SubscriptionDirection Direction, SubscriptionRequestStatus Status,
        DateTimeOffset RequestedAt, DateTimeOffset? GrantedAt);

    public record MessageTypeView(string MessageType, MessageKind? Kind, RouteKind? RouteKind, ValidationStatus? RouteStatus, IReadOnlyList<string> DeclaredPublishers);

    public record CandidateView(string Endpoint, IReadOnlyDictionary<string, CapabilityState> Sites);

    public record RouteView(string MessageType, RouteKind Kind, string Destination, string Publisher, string PreviousPublisher, DateTimeOffset? MigrationStartedAt, IReadOnlyList<string> Subscribers,
        string ChangedBy, DateTimeOffset ChangedAt, string Comment, string Version, RouteValidation Validation);
}
