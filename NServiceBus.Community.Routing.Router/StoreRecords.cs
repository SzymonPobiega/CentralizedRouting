using System;
using System.Collections.Generic;
using NServiceBus.Community.Routing.Model;

namespace NServiceBus.Community.Routing.Router
{
    /// <summary>
    /// An endpoint owned by this router, with all its instances. Stored as one document.
    /// </summary>
    public class EndpointRecord
    {
        public string Name { get; set; }
        public AddressPolicy AddressPolicy { get; set; }
        public List<InstanceRecord> Instances { get; set; } = new List<InstanceRecord>();
        public List<EndpointSiteRecord> Sites { get; set; } = new List<EndpointSiteRecord>();

        /// <summary>
        /// Optimistic concurrency token assigned by the store. Null for an endpoint that has not been saved yet.
        /// </summary>
        public string Version { get; set; }
    }

    public class InstanceRecord
    {
        public string InstanceId { get; set; }
        public string SiteId { get; set; }
        public string InputQueue { get; set; }
        public string HostName { get; set; }
        public bool Disabled { get; set; }
        public bool Stopped { get; set; }
        public string CapabilityHash { get; set; }
        public List<HandledType> HandledTypes { get; set; } = new List<HandledType>();
        public List<PublishedType> PublishedTypes { get; set; } = new List<PublishedType>();
        public long DocumentRevision { get; set; }

        /// <summary>
        /// When the report was sent, by the instance's clock. Used to drop reports that arrive out of order.
        /// </summary>
        public DateTimeOffset LastReportSentAt { get; set; }

        /// <summary>
        /// When the report was received, by the router's clock. Used for liveness.
        /// </summary>
        public DateTimeOffset LastReportAt { get; set; }
    }

    public class EndpointSiteRecord
    {
        public string SiteId { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public bool Disabled { get; set; }
    }

    public enum RouteKind
    {
        Command,
        Event
    }

    public class RouteRecord
    {
        public string MessageType { get; set; }
        public RouteKind Kind { get; set; }

        /// <summary>
        /// Destination endpoint of a command route.
        /// </summary>
        public string Destination { get; set; }

        /// <summary>
        /// Single publisher of an event route.
        /// </summary>
        public string Publisher { get; set; }

        /// <summary>
        /// While the event's publisher migrates to <see cref="Publisher"/>, the endpoint it migrates from. Both may publish.
        /// </summary>
        public string PreviousPublisher { get; set; }

        public DateTimeOffset? MigrationStartedAt { get; set; }

        public List<string> Subscribers { get; set; } = new List<string>();
        public string ChangedBy { get; set; }
        public DateTimeOffset ChangedAt { get; set; }
        public string Comment { get; set; }
        public string Version { get; set; }
    }

    public enum IdentitySource
    {
        Configured,
        Learned
    }

    public class SiteIdentityRecord
    {
        public string SiteId { get; set; }
        public string Identity { get; set; }
        public IdentitySource Source { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
    }
}
