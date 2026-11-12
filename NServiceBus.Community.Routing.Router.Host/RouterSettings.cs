using System;
using System.Collections.Generic;
using NServiceBus;

namespace NServiceBus.Community.Routing.Router.Host
{
    /// <summary>
    /// The router's configuration, bound from the <c>Router</c> section. With environment variables the levels are separated
    /// by <c>__</c>, e.g. <c>Router__Interfaces__hq__ConnectionString</c>. Settings left out keep the router's defaults.
    /// </summary>
    public class RouterSettings
    {
        public string Name { get; set; }
        public TimeSpan? HeartbeatInterval { get; set; }
        public int? MissedHeartbeatsBeforeStale { get; set; }
        public TimeSpan? RefreshInterval { get; set; }
        public int? MaxHops { get; set; }
        public int? ImmediateRetries { get; set; }
        public TimeSpan? InterfaceReconnectInterval { get; set; }
        public TimeSpan? UnavailableSiteRetryInterval { get; set; }
        public TimeSpan? PeerHeartbeatInterval { get; set; }
        public int? MissedPeerHeartbeatsBeforeOffline { get; set; }
        public StorageSettings Storage { get; set; } = new StorageSettings();

        /// <summary>
        /// Keyed by interface name.
        /// </summary>
        public Dictionary<string, InterfaceSettings> Interfaces { get; set; } = new Dictionary<string, InterfaceSettings>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Keyed by peer router name.
        /// </summary>
        public Dictionary<string, PeerSettings> Peers { get; set; } = new Dictionary<string, PeerSettings>(StringComparer.OrdinalIgnoreCase);
    }

    public enum StorageType
    {
        InMemory,
        SqlServer,
        PostgreSql
    }

    public class StorageSettings
    {
        /// <summary>
        /// InMemory, the default, keeps nothing across restarts.
        /// </summary>
        public StorageType Type { get; set; } = StorageType.InMemory;
        public string ConnectionString { get; set; }
    }

    public enum TransportType
    {
        SqlServer,
        PostgreSql,
        RabbitMQ,
        AzureServiceBus,
        Learning
    }

    public class InterfaceSettings
    {
        public TransportType? Type { get; set; }

        /// <summary>
        /// The transport's connection string. For the learning transport, its storage directory.
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Site ID. Defaults to {RouterName}.{InterfaceName}.
        /// </summary>
        public string Site { get; set; }

        /// <summary>
        /// Identities endpoints on the site compute, in addition to the one derived from <see cref="ConnectionString"/>.
        /// Needed when the router reaches the site by another host name than the endpoints, e.g. a Docker service name.
        /// </summary>
        public List<string> SiteIdentities { get; set; } = new List<string>();

        public string ErrorQueue { get; set; }
        public int? MaxSnapshotPartSize { get; set; }

        /// <summary>
        /// The router's queue on the site as endpoints address it, used until the router first connects to the site.
        /// Defaults to the router's name.
        /// </summary>
        public string RouterAddress { get; set; }

        /// <summary>
        /// Defaults to SendsAtomicWithReceive for SQL Server and PostgreSQL, which forwards without distributed transactions,
        /// and to the transport's default otherwise.
        /// </summary>
        public TransportTransactionMode? TransactionMode { get; set; }

        /// <summary>
        /// SQL Server and PostgreSQL: the schema of the queue tables.
        /// </summary>
        public string Schema { get; set; }

        /// <summary>
        /// RabbitMQ: Quorum (the default) or Classic, for the queues the router creates.
        /// </summary>
        public QueueType QueueType { get; set; } = QueueType.Quorum;

        /// <summary>
        /// SQL Server: forward each message into this site at most once (table dbo.RouterDeduplication in the site's database).
        /// </summary>
        public bool Deduplication { get; set; }
    }

    /// <summary>
    /// Set exactly one of <see cref="Interface"/>, <see cref="Url"/> and <see cref="Inbound"/>.
    /// </summary>
    public class PeerSettings
    {
        /// <summary>
        /// Exchange snapshots through the site of this interface, which both routers are attached to.
        /// </summary>
        public string Interface { get; set; }

        /// <summary>
        /// Call the peer's HTTP API at this address.
        /// </summary>
        public Uri Url { get; set; }

        /// <summary>
        /// The peer calls this router's HTTP API.
        /// </summary>
        public bool Inbound { get; set; }
    }
}
