using System;
using System.Collections.Generic;
using System.Linq;
using CentralRouting.Model;
using NServiceBus;
using NServiceBus.Transport;

namespace CentralRouting.Router.Host
{
    public static class RouterSettingsExtensions
    {
        /// <summary>
        /// Builds the router's configuration. Throws one exception listing every problem found in the settings.
        /// </summary>
        public static RouterConfiguration ToRouterConfiguration(this RouterSettings settings)
        {
            var errors = Validate(settings).ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"The router configuration is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
            }

            var router = new RouterConfiguration(settings.Name);
            if (settings.HeartbeatInterval is { } heartbeatInterval) router.HeartbeatInterval = heartbeatInterval;
            if (settings.MissedHeartbeatsBeforeStale is { } missedHeartbeats) router.MissedHeartbeatsBeforeStale = missedHeartbeats;
            if (settings.RefreshInterval is { } refreshInterval) router.RefreshInterval = refreshInterval;
            if (settings.MaxHops is { } maxHops) router.MaxHops = maxHops;
            if (settings.ImmediateRetries is { } immediateRetries) router.ImmediateRetries = immediateRetries;
            if (settings.PeerHeartbeatInterval is { } peerHeartbeatInterval) router.PeerHeartbeatInterval = peerHeartbeatInterval;
            if (settings.MissedPeerHeartbeatsBeforeOffline is { } missedPeerHeartbeats) router.MissedPeerHeartbeatsBeforeOffline = missedPeerHeartbeats;

            switch (settings.Storage.Type)
            {
                case StorageType.SqlServer:
                    router.UseSqlServerStorage(settings.Storage.ConnectionString);
                    break;
                case StorageType.PostgreSql:
                    router.UsePostgreSqlStorage(settings.Storage.ConnectionString);
                    break;
            }

            foreach (var (name, interfaceSettings) in settings.Interfaces)
            {
                var type = interfaceSettings.Type!.Value;
                var routerInterface = router.AddInterface(name, CreateTransport(type, interfaceSettings));
                if (!string.IsNullOrWhiteSpace(interfaceSettings.Site)) routerInterface.Site = interfaceSettings.Site;
                if (interfaceSettings.ErrorQueue != null) routerInterface.ErrorQueue = interfaceSettings.ErrorQueue;
                if (interfaceSettings.MaxSnapshotPartSize is { } maxPartSize) routerInterface.MaxSnapshotPartSize = maxPartSize;

                var identities = interfaceSettings.SiteIdentities.Prepend(DeriveIdentity(type, interfaceSettings));
                foreach (var identity in identities.Where(i => i != null && !routerInterface.SiteIdentities.Contains(i)))
                {
                    routerInterface.SiteIdentities.Add(identity);
                }

                if (interfaceSettings.Deduplication)
                {
                    router.AddSqlServerDeduplication(routerInterface.Site, interfaceSettings.ConnectionString);
                }
            }

            foreach (var (name, peer) in settings.Peers)
            {
                if (peer.Interface != null)
                {
                    router.AddPeer(name, peer.Interface);
                }
                else if (peer.Url != null)
                {
                    router.AddHttpPeer(name, peer.Url);
                }
                else
                {
                    router.AddInboundPeer(name);
                }
            }

            return router;
        }

        static IEnumerable<string> Validate(RouterSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.Name))
            {
                yield return "Router:Name is required.";
            }

            if (settings.Storage.Type != StorageType.InMemory && string.IsNullOrWhiteSpace(settings.Storage.ConnectionString))
            {
                yield return $"Router:Storage:ConnectionString is required for {settings.Storage.Type} storage.";
            }

            if (settings.Interfaces.Count == 0)
            {
                yield return "Configure at least one interface under Router:Interfaces.";
            }

            foreach (var (name, interfaceSettings) in settings.Interfaces)
            {
                var prefix = $"Router:Interfaces:{name}";
                if (interfaceSettings.Type == null)
                {
                    yield return $"{prefix}:Type is required: one of {string.Join(", ", Enum.GetNames<TransportType>())}.";
                    continue;
                }

                if (string.IsNullOrWhiteSpace(interfaceSettings.ConnectionString))
                {
                    yield return interfaceSettings.Type == TransportType.Learning
                        ? $"{prefix}:ConnectionString is required: the learning transport's storage directory."
                        : $"{prefix}:ConnectionString is required.";
                }

                if (interfaceSettings.Deduplication && interfaceSettings.Type != TransportType.SqlServer)
                {
                    yield return $"{prefix}:Deduplication is only supported on SQL Server interfaces.";
                }

                if (interfaceSettings.Type == TransportType.PostgreSql && interfaceSettings.SiteIdentities.Count == 0)
                {
                    yield return $"{prefix}:SiteIdentities is required for PostgreSQL, whose site identity is not derived from the connection string.";
                }
            }

            foreach (var (name, peer) in settings.Peers)
            {
                var channels = (peer.Interface != null ? 1 : 0) + (peer.Url != null ? 1 : 0) + (peer.Inbound ? 1 : 0);
                if (channels != 1)
                {
                    yield return $"Router:Peers:{name} needs exactly one of Interface, Url and Inbound.";
                }
                else if (peer.Interface != null && !settings.Interfaces.ContainsKey(peer.Interface))
                {
                    yield return $"Router:Peers:{name}:Interface names '{peer.Interface}', which is not configured under Router:Interfaces.";
                }
            }
        }

        static TransportDefinition CreateTransport(TransportType type, InterfaceSettings settings)
        {
            var connectionString = settings.ConnectionString;
            TransportDefinition transport = type switch
            {
                TransportType.SqlServer => new SqlServerTransport(connectionString) { DefaultSchema = settings.Schema ?? "dbo" },
                TransportType.PostgreSql => new PostgreSqlTransport(connectionString) { DefaultSchema = settings.Schema ?? "public" },
                TransportType.RabbitMQ => new RabbitMQTransport(RoutingTopology.Conventional(settings.QueueType), connectionString),
                TransportType.AzureServiceBus => new AzureServiceBusTransport(connectionString, TopicTopology.Default),
                TransportType.Learning => new LearningTransport { StorageDirectory = connectionString },
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };

            if (settings.TransactionMode is { } mode)
            {
                transport.TransportTransactionMode = mode;
            }
            else if (type is TransportType.SqlServer or TransportType.PostgreSql)
            {
                //No distributed transactions: the router forwards between different transports
                transport.TransportTransactionMode = TransportTransactionMode.SendsAtomicWithReceive;
            }

            return transport;
        }

        /// <summary>
        /// The identity endpoints on the site compute from the same connection. The learning transport's is added by the router itself.
        /// </summary>
        static string DeriveIdentity(TransportType type, InterfaceSettings settings)
        {
            return type switch
            {
                TransportType.SqlServer => SiteIdentity.ForSqlServer(settings.ConnectionString, settings.Schema ?? "dbo"),
                TransportType.RabbitMQ => SiteIdentity.ForRabbitMQ(settings.ConnectionString),
                TransportType.AzureServiceBus => SiteIdentity.ForAzureServiceBus(settings.ConnectionString),
                _ => null
            };
        }
    }
}
