using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralRouting.Model;
using Microsoft.Extensions.Logging;
using NServiceBus.Transport;

namespace CentralRouting.Router
{
    /// <summary>
    /// Handles capability reports, heartbeats and shutdown notifications sent by endpoint instances.
    /// </summary>
    class ControlMessageHandler
    {
        readonly IRouterStore store;
        readonly RoutingSnapshotProvider snapshots;
        readonly RouterConfiguration configuration;
        readonly PeeringService peering;
        readonly ILogger logger;

        public ControlMessageHandler(IRouterStore store, RoutingSnapshotProvider snapshots, RouterConfiguration configuration, PeeringService peering,
            ILogger<ControlMessageHandler> logger)
        {
            this.store = store;
            this.snapshots = snapshots;
            this.configuration = configuration;
            this.peering = peering;
            this.logger = logger;
        }

        public async Task Handle(RouterInterface routerInterface, MessageContext message, CancellationToken cancellationToken)
        {
            if (!message.Headers.TryGetValue(RoutingHeaders.ControlMessageType, out var messageType))
            {
                throw new UnforwardableMessageException($"Message {message.NativeMessageId} received on the control queue is not a control message.");
            }

            if (messageType == ControlMessageTypes.PeerSnapshot)
            {
                await peering.ReceivePart(message.Headers, message.Body, cancellationToken).ConfigureAwait(false);
                return;
            }

            var changed = messageType switch
            {
                ControlMessageTypes.CapabilityReport => await HandleReport(routerInterface,
                    RoutingJson.Deserialize<CapabilityReport>(message.Body.ToArray()), cancellationToken).ConfigureAwait(false),
                ControlMessageTypes.InstanceStopping => await HandleStopping(
                    RoutingJson.Deserialize<InstanceStopping>(message.Body.ToArray()), cancellationToken).ConfigureAwait(false),
                _ => throw new UnforwardableMessageException($"Control message type '{messageType}' is not recognized.")
            };

            var snapshot = changed
                ? await snapshots.Changed(cancellationToken).ConfigureAwait(false)
                : await snapshots.Refresh(false, cancellationToken).ConfigureAwait(false);

            if (await AssignPublishers(snapshot.Model, cancellationToken).ConfigureAwait(false))
            {
                await snapshots.Changed(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Makes an endpoint the publisher of the events only it declares. Runs on every report, so an assignment lost
        /// to a concurrent change is retried with the next heartbeat.
        /// </summary>
        async Task<bool> AssignPublishers(RoutingModel model, CancellationToken cancellationToken)
        {
            var assigned = false;
            foreach (var (messageType, publisher) in model.GetPublisherAssignments())
            {
                var route = await store.GetRoute(messageType, cancellationToken).ConfigureAwait(false)
                            ?? new RouteRecord { MessageType = messageType, Kind = RouteKind.Event };
                if (route.Kind != RouteKind.Event || route.Publisher != null)
                {
                    continue;
                }

                route.Publisher = publisher;
                route.ChangedBy = configuration.Name;
                route.ChangedAt = DateTimeOffset.UtcNow;
                route.Comment = $"{publisher} declares that it publishes the event";

                try
                {
                    await store.SaveRoute(route, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("Assigned {Publisher} as the publisher of {MessageType}", publisher, messageType);
                    assigned = true;
                }
                catch (StoreConcurrencyException)
                {
                }
            }
            return assigned;
        }

        async Task<bool> HandleReport(RouterInterface routerInterface, CapabilityReport report, CancellationToken cancellationToken)
        {
            var site = routerInterface.SiteId;
            var identityLearned = await LearnIdentity(site, report.SiteIdentity, cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var documentChanged = false;
            await store.UpdateEndpoint(report.EndpointName, true, endpoint =>
            {
                documentChanged = false;
                var instance = endpoint.Instances.FirstOrDefault(i => i.InstanceId == report.InstanceId);
                if (instance == null)
                {
                    instance = new InstanceRecord { InstanceId = report.InstanceId };
                    endpoint.Instances.Add(instance);
                    documentChanged = true;
                }
                else if (report.SentAt < instance.LastReportSentAt)
                {
                    //An older report that arrived late
                    return false;
                }

                documentChanged |= instance.SiteId != site
                                   || instance.InputQueue != report.InputQueue
                                   || instance.CapabilityHash != report.CapabilityHash
                                   || instance.Stopped
                                   || now - instance.LastReportAt > configuration.InstanceExpiry;

                instance.SiteId = site;
                instance.InputQueue = report.InputQueue;
                instance.HostName = report.HostName;
                instance.Stopped = false;
                instance.DocumentRevision = report.DocumentRevision;
                instance.LastReportSentAt = report.SentAt;
                instance.LastReportAt = now;
                if (instance.CapabilityHash != report.CapabilityHash)
                {
                    instance.CapabilityHash = report.CapabilityHash;
                    instance.HandledTypes = report.HandledTypes;
                    instance.PublishedTypes = report.PublishedTypes;
                }

                if (endpoint.Sites.All(s => s.SiteId != site))
                {
                    endpoint.Sites.Add(new EndpointSiteRecord { SiteId = site, FirstSeen = now });
                    documentChanged = true;
                }
                return true;
            }, cancellationToken).ConfigureAwait(false);

            return identityLearned || documentChanged;
        }

        async Task<bool> HandleStopping(InstanceStopping message, CancellationToken cancellationToken)
        {
            return await store.UpdateEndpoint(message.EndpointName, false, endpoint =>
            {
                var instance = endpoint.Instances.FirstOrDefault(i => i.InstanceId == message.InstanceId);
                if (instance == null || instance.Stopped || message.SentAt < instance.LastReportSentAt)
                {
                    return false;
                }
                instance.Stopped = true;
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        async Task<bool> LearnIdentity(string site, string identity, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(identity))
            {
                return false;
            }

            var owner = configuration.Interfaces
                .Where(i => i.SiteIdentities.Contains(identity, StringComparer.OrdinalIgnoreCase))
                .Select(i => i.Site)
                .FirstOrDefault();

            if (owner == null)
            {
                var identities = await store.GetSiteIdentities(cancellationToken).ConfigureAwait(false);
                owner = identities.FirstOrDefault(i => string.Equals(i.Identity, identity, StringComparison.OrdinalIgnoreCase))?.SiteId;
            }

            if (owner == site)
            {
                return false;
            }

            if (owner != null)
            {
                logger.LogWarning("Site identity {Identity} reported through site {Site} already belongs to site {Owner}", identity, site, owner);
                return false;
            }

            await store.AddSiteIdentity(new SiteIdentityRecord
            {
                SiteId = site,
                Identity = identity,
                Source = IdentitySource.Learned,
                FirstSeen = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }
}
