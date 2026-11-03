using System;
using System.Collections.Generic;
using CentralRouting.Model;

namespace CentralRouting.Router
{
    /// <summary>
    /// What a router tells its peers. Always complete: anything missing from it is gone.
    /// A router exports only what it owns, never what it learned, so information travels one hop.
    /// </summary>
    public class PeerSnapshot
    {
        public string Router { get; set; }
        public long Revision { get; set; }
        public DateTimeOffset SentAt { get; set; }
        public List<PeerSite> Sites { get; set; } = new List<PeerSite>();
        public List<PeerEndpoint> Endpoints { get; set; } = new List<PeerEndpoint>();
        public List<PublishedEvent> PublishedEvents { get; set; } = new List<PublishedEvent>();

        /// <summary>
        /// Subscription requests this router sends, each addressed to the router that owns the publisher.
        /// </summary>
        public List<PeerSubscription> Requests { get; set; } = new List<PeerSubscription>();

        /// <summary>
        /// Requests of other routers this router has granted.
        /// </summary>
        public List<PeerSubscription> Grants { get; set; } = new List<PeerSubscription>();
    }

    /// <summary>
    /// A site the router is attached to and its own queue there.
    /// </summary>
    public class PeerSite
    {
        public string SiteId { get; set; }
        public string RouterAddress { get; set; }
        public List<string> Identities { get; set; } = new List<string>();
    }

    public class PeerEndpoint
    {
        public string Name { get; set; }
        public AddressPolicy Policy { get; set; }
        public Dictionary<string, Dictionary<string, int>> Addresses { get; set; } = new Dictionary<string, Dictionary<string, int>>();
        public Dictionary<string, Dictionary<string, CapabilityState>> Capabilities { get; set; } = new Dictionary<string, Dictionary<string, CapabilityState>>();
    }

    public class PublishedEvent
    {
        public string MessageType { get; set; }
        public string Publisher { get; set; }
    }

    public class PeerSubscription
    {
        public string MessageType { get; set; }

        /// <summary>
        /// The other router: the target of a request, the requester of a grant.
        /// </summary>
        public string Router { get; set; }
    }

    /// <summary>
    /// The last snapshot applied from a configured peer.
    /// </summary>
    public class PeerRecord
    {
        public string Name { get; set; }
        public PeerSnapshot Snapshot { get; set; }
        public DateTimeOffset LastReceivedAt { get; set; }
        public string Version { get; set; }
    }

    public enum SubscriptionDirection
    {
        /// <summary>
        /// This router asked the publisher's router for an event.
        /// </summary>
        Outgoing,

        /// <summary>
        /// A peer asked this router for an event it publishes.
        /// </summary>
        Incoming
    }

    public enum SubscriptionRequestStatus
    {
        Requested,
        Granting,
        Granted
    }

    public class SubscriptionRequestRecord
    {
        public string MessageType { get; set; }
        public string Peer { get; set; }
        public SubscriptionDirection Direction { get; set; }
        public SubscriptionRequestStatus Status { get; set; }

        /// <summary>
        /// Document revision that tells publishers to forward the event. The grant completes once they all run it.
        /// </summary>
        public long GrantRevision { get; set; }

        public DateTimeOffset RequestedAt { get; set; }
        public DateTimeOffset? GrantedAt { get; set; }
        public string Version { get; set; }

        public string Key => $"{Direction}|{Peer}|{MessageType}";
    }
}
