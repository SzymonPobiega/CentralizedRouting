using System;
using System.Collections.Generic;

namespace CentralRouting.Model
{
    public enum MessageKind
    {
        Command,
        Event,
        Message
    }

    /// <summary>
    /// Sent by each endpoint instance at startup and then periodically as a heartbeat.
    /// </summary>
    public class CapabilityReport
    {
        public string EndpointName { get; set; }
        public string InstanceId { get; set; }
        public string HostName { get; set; }
        public string InputQueue { get; set; }
        public string SiteIdentity { get; set; }
        public string CapabilityHash { get; set; }
        public long DocumentRevision { get; set; }
        public DateTimeOffset SentAt { get; set; }
        public List<HandledType> HandledTypes { get; set; } = new List<HandledType>();

        /// <summary>
        /// Events the endpoint declares it publishes. The router assigns it as their publisher when no other endpoint declares them.
        /// </summary>
        public List<PublishedType> PublishedTypes { get; set; } = new List<PublishedType>();
    }

    public class PublishedType
    {
        public string Type { get; set; }
        public List<string> BaseTypes { get; set; } = new List<string>();
    }

    public class HandledType
    {
        public string Type { get; set; }
        public MessageKind Kind { get; set; }
        public List<string> BaseTypes { get; set; } = new List<string>();
        public List<string> Handlers { get; set; } = new List<string>();
    }

    /// <summary>
    /// Sent by an endpoint instance when it shuts down cleanly.
    /// </summary>
    public class InstanceStopping
    {
        public string EndpointName { get; set; }
        public string InstanceId { get; set; }
        public DateTimeOffset SentAt { get; set; }
    }
}
