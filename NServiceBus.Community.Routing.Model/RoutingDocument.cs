using System;
using System.Collections.Generic;

namespace NServiceBus.Community.Routing.Model
{
    public enum AddressPolicy
    {
        Strict,
        Distribute,
        PreferLocal,
        Explicit,
        NewestSite
    }

    /// <summary>
    /// Routing information published by a router for the endpoints it owns.
    /// </summary>
    public class RoutingDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public string Router { get; set; }
        public long Revision { get; set; }
        public DateTimeOffset GeneratedAt { get; set; }
        public Dictionary<string, SiteInfo> Sites { get; set; } = new Dictionary<string, SiteInfo>();
        public Dictionary<string, EndpointInfo> Endpoints { get; set; } = new Dictionary<string, EndpointInfo>();
        public Dictionary<string, CommandRoute> Routes { get; set; } = new Dictionary<string, CommandRoute>();
        public Dictionary<string, EventRoute> Events { get; set; } = new Dictionary<string, EventRoute>();
    }

    public class SiteInfo
    {
        public List<string> Identities { get; set; } = new List<string>();

        /// <summary>
        /// Transport address of the publishing router's own queue on this site.
        /// </summary>
        public string Router { get; set; }

        /// <summary>
        /// Maps a destination site to the transport address, on this site, of the router queue that leads there.
        /// </summary>
        public Dictionary<string, string> NextHop { get; set; } = new Dictionary<string, string>();
    }

    public class EndpointInfo
    {
        public AddressPolicy Policy { get; set; }

        /// <summary>
        /// Maps a site to the endpoint's queues on that site and the number of live instances behind each queue.
        /// </summary>
        public Dictionary<string, Dictionary<string, int>> Addresses { get; set; } = new Dictionary<string, Dictionary<string, int>>();
    }

    public class CommandRoute
    {
        public string Endpoint { get; set; }
    }

    public class EventRoute
    {
        public string Publisher { get; set; }

        /// <summary>
        /// While the publisher migrates, the endpoint it migrates from. It may still publish the event.
        /// </summary>
        public string PreviousPublisher { get; set; }

        public List<string> Subscribers { get; set; } = new List<string>();

        /// <summary>
        /// At least one peer router holds a grant for the event: the publisher also sends one copy to its own router.
        /// </summary>
        public bool Forward { get; set; }
    }
}
