using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NServiceBus.Community.Routing.Router
{
    /// <summary>
    /// Tells a site that is down from a message that can't be dispatched. When a dispatch to a site fails, the router probes
    /// the site by sending to its own control queue there: if the probe fails too, the site is unreachable.
    /// Logs when a site becomes unreachable and when it is back.
    /// </summary>
    class SiteAvailability
    {
        /// <summary>
        /// Messages that fail at the same time share one probe.
        /// </summary>
        static readonly TimeSpan probeValidity = TimeSpan.FromSeconds(1);

        readonly RouterTopology topology;
        readonly RouterConfiguration configuration;
        readonly ILogger logger;
        readonly ConcurrentDictionary<string, SiteState> sites = new ConcurrentDictionary<string, SiteState>();

        public SiteAvailability(RouterTopology topology, RouterConfiguration configuration, ILogger<SiteAvailability> logger)
        {
            this.topology = topology;
            this.configuration = configuration;
            this.logger = logger;
        }

        public bool IsConnected(string site) => topology.TryGetInterface(site, out var routerInterface) && routerInterface.IsConnected;

        /// <summary>
        /// Whether the site was unreachable when last probed or dispatched to.
        /// </summary>
        public bool IsUnavailable(string site) => sites.TryGetValue(site, out var state) && state.Unavailable;

        public async Task<bool> IsReachable(string site, Exception failure, CancellationToken cancellationToken)
        {
            if (!topology.TryGetInterface(site, out var routerInterface) || !routerInterface.IsConnected)
            {
                return false;
            }

            var state = sites.GetOrAdd(site, _ => new SiteState());
            Task<bool> probe;
            lock (state)
            {
                if (state.Probe == null || DateTimeOffset.UtcNow - state.ProbeStartedAt > probeValidity)
                {
                    state.ProbeStartedAt = DateTimeOffset.UtcNow;
                    state.Probe = Probe(routerInterface, failure, cancellationToken);
                }
                probe = state.Probe;
            }

            return await probe.ConfigureAwait(false);
        }

        public void Succeeded(string site)
        {
            if (sites.TryGetValue(site, out var state) && state.Unavailable)
            {
                lock (state)
                {
                    if (!state.Unavailable)
                    {
                        return;
                    }
                    state.Unavailable = false;
                }
                logger.LogInformation("Site {Site} is reachable again", site);
            }
        }

        void Failed(string site, Exception failure)
        {
            var state = sites.GetOrAdd(site, _ => new SiteState());
            lock (state)
            {
                if (state.Unavailable)
                {
                    return;
                }
                state.Unavailable = true;
            }
            logger.LogWarning(failure, "Site {Site} is unreachable. Messages for it wait in their queues and are retried every {Interval}",
                site, configuration.UnavailableSiteRetryInterval);
        }

        async Task<bool> Probe(RouterInterface routerInterface, Exception failure, CancellationToken cancellationToken)
        {
            try
            {
                await routerInterface.Probe(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Failed(routerInterface.SiteId, failure);
                return false;
            }
        }

        class SiteState
        {
            public bool Unavailable;
            public Task<bool> Probe;
            public DateTimeOffset ProbeStartedAt;
        }
    }
}
