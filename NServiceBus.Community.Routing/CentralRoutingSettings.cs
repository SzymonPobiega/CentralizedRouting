using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace NServiceBus.Community.Routing
{
    /// <summary>
    /// Settings of an endpoint that uses central routing.
    /// </summary>
    public class CentralRoutingSettings
    {
        internal CentralRoutingSettings(string routerName)
        {
            RouterName = routerName;
        }

        /// <summary>
        /// Name of the router the endpoint reports to.
        /// </summary>
        public string RouterName { get; }

        /// <summary>
        /// Configuration that contains the routing document. When not set, the endpoint builds one from <see cref="Document"/>.
        /// </summary>
        public IConfiguration Configuration { get; set; }

        /// <summary>
        /// Where the routing document comes from when <see cref="Configuration"/> is not set.
        /// </summary>
        public RoutingDocumentSourceOptions Document { get; } = new RoutingDocumentSourceOptions();

        /// <summary>
        /// Identity of the site the endpoint is attached to. Detected for the learning transport; required for other transports.
        /// </summary>
        public string SiteIdentity { get; set; }

        /// <summary>
        /// Unique ID of this instance. Defaults to the host ID.
        /// </summary>
        public string InstanceId { get; set; }

        /// <summary>
        /// How often the instance re-sends its capability report.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Declares that the endpoint publishes the event. The router makes it the publisher when no other endpoint declares
        /// the event, and the endpoint refuses to publish events it does not declare.
        /// </summary>
        public CentralRoutingSettings Publishes<TEvent>() => Publishes(typeof(TEvent));

        /// <summary>
        /// Declares that the endpoint publishes the event.
        /// </summary>
        public CentralRoutingSettings Publishes(Type eventType)
        {
            ArgumentNullException.ThrowIfNull(eventType);
            publishedTypes.Add(eventType);
            return this;
        }

        internal IReadOnlySet<Type> PublishedTypes => publishedTypes;

        readonly HashSet<Type> publishedTypes = new HashSet<Type>();
    }
}
