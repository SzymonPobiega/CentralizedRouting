using System;
using System.Collections.Generic;
using NServiceBus;
using NServiceBus.Transport;

namespace NServiceBus.Community.Routing.Router
{
    /// <summary>
    /// Configures a router.
    /// </summary>
    public class RouterConfiguration
    {
        internal readonly List<InterfaceConfiguration> Interfaces = new List<InterfaceConfiguration>();
        internal readonly List<PeerConfiguration> Peers = new List<PeerConfiguration>();
        internal readonly List<Func<IServiceProvider, IForwardingBehavior>> ForwardingBehaviors = new List<Func<IServiceProvider, IForwardingBehavior>>();
        internal Func<IServiceProvider, IRouterStore> StoreFactory = _ => new InMemoryRouterStore();

        /// <summary>
        /// Creates a router configuration. The router owns the queues named after it on each interface.
        /// </summary>
        public RouterConfiguration(string name)
        {
            Name = name;
        }

        public string Name { get; }

        /// <summary>
        /// Expected interval between instance heartbeats.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Number of missed heartbeats after which an instance is considered stale.
        /// </summary>
        public int MissedHeartbeatsBeforeStale { get; set; } = 3;

        /// <summary>
        /// How often the router checks the store for changes made by other processes and re-evaluates instance liveness.
        /// </summary>
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Maximum number of routers a message may pass through.
        /// </summary>
        public int MaxHops { get; set; } = 8;

        /// <summary>
        /// Number of immediate retries before a message that cannot be forwarded is moved to the error queue.
        /// </summary>
        public int ImmediateRetries { get; set; } = 5;

        /// <summary>
        /// How often the router tries again to connect an interface whose site was unreachable.
        /// The router starts without such an interface and attaches to the site once it connects.
        /// </summary>
        public TimeSpan InterfaceReconnectInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long a message whose destination site is unreachable waits before the router retries it.
        /// Such a message stays in the queue it was received from instead of moving to the error queue.
        /// </summary>
        public TimeSpan UnavailableSiteRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Adds an interface that connects the router to a site.
        /// </summary>
        public InterfaceConfiguration AddInterface(string name, TransportDefinition transport)
        {
            var interfaceConfiguration = new InterfaceConfiguration(name, $"{Name}.{name}", transport);
            if (transport is LearningTransport learning && learning.StorageDirectory != null)
            {
                interfaceConfiguration.SiteIdentities.Add(Model.SiteIdentity.ForLearningTransport(learning.StorageDirectory));
            }
            Interfaces.Add(interfaceConfiguration);
            return interfaceConfiguration;
        }

        /// <summary>
        /// How often the router re-sends its snapshot to peers when nothing changed.
        /// </summary>
        public TimeSpan PeerHeartbeatInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Number of missed snapshots after which a peer is considered offline. Its data is kept.
        /// </summary>
        public int MissedPeerHeartbeatsBeforeOffline { get; set; } = 3;

        /// <summary>
        /// Sets the store used by the router. The default is a non-durable in-memory store.
        /// </summary>
        public void UseStorage(Func<IServiceProvider, IRouterStore> storeFactory)
        {
            StoreFactory = storeFactory;
        }

        /// <summary>
        /// Adds a peer that exchanges snapshots through a site both routers are attached to.
        /// The snapshot is sent to the peer's control queue on that site.
        /// </summary>
        public PeerConfiguration AddPeer(string name, string interfaceName)
        {
            return AddPeer(new PeerConfiguration(name, PeerChannel.Interface) { InterfaceName = interfaceName });
        }

        /// <summary>
        /// Adds a peer whose HTTP API this router calls. One call sends this router's snapshot and returns the peer's.
        /// </summary>
        public PeerConfiguration AddHttpPeer(string name, Uri routerUrl)
        {
            return AddPeer(new PeerConfiguration(name, PeerChannel.Http) { Url = routerUrl });
        }

        /// <summary>
        /// Adds a peer that calls this router's HTTP API. Use on the reachable side of an HTTP peering.
        /// </summary>
        public PeerConfiguration AddInboundPeer(string name)
        {
            return AddPeer(new PeerConfiguration(name, PeerChannel.Inbound));
        }

        /// <summary>
        /// Adds a behavior to the forwarding pipeline. Behaviors run in the order they are added.
        /// </summary>
        public void AddForwardingBehavior(IForwardingBehavior behavior)
        {
            ForwardingBehaviors.Add(_ => behavior);
        }

        /// <summary>
        /// Adds a behavior created from the service provider to the forwarding pipeline.
        /// </summary>
        public void AddForwardingBehavior<T>() where T : IForwardingBehavior
        {
            ForwardingBehaviors.Add(sp => Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<T>(sp));
        }

        /// <summary>
        /// Adds a behavior created by a factory, e.g. one that needs a logger and settings of its own.
        /// </summary>
        public void AddForwardingBehavior(Func<IServiceProvider, IForwardingBehavior> factory)
        {
            ForwardingBehaviors.Add(factory);
        }

        PeerConfiguration AddPeer(PeerConfiguration peer)
        {
            Peers.Add(peer);
            return peer;
        }

        internal TimeSpan InstanceExpiry => TimeSpan.FromTicks(HeartbeatInterval.Ticks * MissedHeartbeatsBeforeStale);
        internal TimeSpan PeerExpiry => TimeSpan.FromTicks(PeerHeartbeatInterval.Ticks * MissedPeerHeartbeatsBeforeOffline);
    }

    public enum PeerChannel
    {
        Interface,
        Http,
        Inbound
    }

    /// <summary>
    /// Configures a peer router.
    /// </summary>
    public class PeerConfiguration
    {
        internal PeerConfiguration(string name, PeerChannel channel)
        {
            Name = name;
            Channel = channel;
        }

        public string Name { get; }
        public PeerChannel Channel { get; }
        public string InterfaceName { get; internal set; }
        public Uri Url { get; internal set; }

        /// <summary>
        /// Custom handler for the HTTP channel, e.g. to add authentication. Optional.
        /// </summary>
        public System.Net.Http.HttpMessageHandler HttpMessageHandler { get; set; }
    }

    /// <summary>
    /// Configures one interface of a router.
    /// </summary>
    public class InterfaceConfiguration
    {
        internal InterfaceConfiguration(string name, string site, TransportDefinition transport)
        {
            Name = name;
            Site = site;
            Transport = transport;
        }

        public string Name { get; }
        public TransportDefinition Transport { get; }

        /// <summary>
        /// Globally unique ID of the site this interface is attached to. Defaults to {RouterName}.{InterfaceName}.
        /// </summary>
        public string Site { get; set; }

        /// <summary>
        /// Identities endpoints on this site compute from their transport configuration.
        /// Identities reported by endpoints through this interface are learned automatically.
        /// </summary>
        public List<string> SiteIdentities { get; } = new List<string>();

        public string ErrorQueue { get; set; } = "error";

        /// <summary>
        /// Address of the router's queue on the site, published in the routing document until the interface first connects
        /// (afterwards the transport's own address is used). Defaults to the router's name, the queue's name on every transport.
        /// </summary>
        public string RouterAddress { get; set; }

        /// <summary>
        /// Maximum size of a snapshot part sent to peers through this interface, in bytes.
        /// </summary>
        public int MaxSnapshotPartSize { get; set; } = 200 * 1024;
    }
}
