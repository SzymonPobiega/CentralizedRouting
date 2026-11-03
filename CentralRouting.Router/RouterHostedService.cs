using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CentralRouting.Router
{
    /// <summary>
    /// Starts the router's interfaces and keeps the routing snapshot up to date.
    /// </summary>
    class RouterHostedService : IHostedService
    {
        readonly RouterConfiguration configuration;
        readonly IRouterStore store;
        readonly RouterTopology topology;
        readonly RoutingSnapshotProvider snapshots;
        readonly ControlMessageHandler controlMessageHandler;
        readonly Forwarder forwarder;
        readonly PeeringService peering;
        readonly ILoggerFactory loggerFactory;
        readonly ILogger logger;
        readonly List<RouterInterface> interfaces = new List<RouterInterface>();
        readonly CancellationTokenSource stopping = new CancellationTokenSource();
        Task refreshLoop;

        public RouterHostedService(RouterConfiguration configuration, IRouterStore store, RouterTopology topology,
            RoutingSnapshotProvider snapshots, ControlMessageHandler controlMessageHandler, Forwarder forwarder, PeeringService peering,
            ILoggerFactory loggerFactory)
        {
            this.configuration = configuration;
            this.store = store;
            this.topology = topology;
            this.snapshots = snapshots;
            this.controlMessageHandler = controlMessageHandler;
            this.forwarder = forwarder;
            this.peering = peering;
            this.loggerFactory = loggerFactory;
            logger = loggerFactory.CreateLogger<RouterHostedService>();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Validate();

            await store.Initialize(cancellationToken).ConfigureAwait(false);

            foreach (var interfaceConfiguration in configuration.Interfaces)
            {
                var routerInterface = new RouterInterface(configuration.Name, interfaceConfiguration, configuration.ImmediateRetries,
                    loggerFactory.CreateLogger($"{typeof(RouterInterface).FullName}.{interfaceConfiguration.Name}"));
                await routerInterface.Initialize(forwarder.Forward, controlMessageHandler.Handle, cancellationToken).ConfigureAwait(false);
                interfaces.Add(routerInterface);
            }

            topology.Initialize(interfaces);
            await snapshots.Refresh(true, cancellationToken).ConfigureAwait(false);

            foreach (var routerInterface in interfaces)
            {
                await routerInterface.StartReceive(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Router {Router} attached to site {Site} through interface {Interface}", configuration.Name, routerInterface.SiteId, routerInterface.Name);
            }

            refreshLoop = Task.Run(() => RefreshLoop(stopping.Token), CancellationToken.None);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            if (refreshLoop != null)
            {
                await refreshLoop.ConfigureAwait(false);
            }

            foreach (var routerInterface in interfaces)
            {
                await routerInterface.Stop(cancellationToken).ConfigureAwait(false);
            }
        }

        async Task RefreshLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(configuration.RefreshInterval, cancellationToken).ConfigureAwait(false);
                    await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false);
                    await snapshots.CheckLiveness(cancellationToken).ConfigureAwait(false);
                    await peering.CompleteGrants(cancellationToken).ConfigureAwait(false);
                    await peering.SendSnapshots(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Failed to refresh the routing state");
                }
            }
        }

        void Validate()
        {
            if (configuration.Interfaces.Count == 0)
            {
                throw new Exception($"Router {configuration.Name} has no interfaces.");
            }

            var duplicateSite = configuration.Interfaces.GroupBy(i => i.Site).FirstOrDefault(g => g.Count() > 1);
            if (duplicateSite != null)
            {
                throw new Exception($"Interfaces {string.Join(", ", duplicateSite.Select(i => i.Name))} are attached to the same site {duplicateSite.Key}.");
            }

            var unknownInterface = configuration.Peers.FirstOrDefault(p => p.Channel == PeerChannel.Interface && configuration.Interfaces.All(i => i.Name != p.InterfaceName));
            if (unknownInterface != null)
            {
                throw new Exception($"Peer {unknownInterface.Name} uses interface {unknownInterface.InterfaceName}, which is not configured.");
            }

            var duplicateIdentity = configuration.Interfaces
                .SelectMany(i => i.SiteIdentities.Select(id => (Identity: id.ToLowerInvariant(), Interface: i.Name)))
                .GroupBy(x => x.Identity)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateIdentity != null)
            {
                throw new Exception($"Interfaces {string.Join(", ", duplicateIdentity.Select(x => x.Interface))} have the same site identity {duplicateIdentity.Key}.");
            }
        }
    }
}
