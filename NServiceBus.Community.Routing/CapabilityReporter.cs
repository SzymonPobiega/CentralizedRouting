using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Model;
using NServiceBus;
using NServiceBus.Features;
using NServiceBus.Logging;
using NServiceBus.Routing;
using NServiceBus.Transport;

namespace NServiceBus.Community.Routing
{
    /// <summary>
    /// Sends the instance's capability report to the router's control queue at startup and then as a heartbeat.
    /// </summary>
    class CapabilityReporter : FeatureStartupTask
    {
        static readonly ILog log = LogManager.GetLogger<CapabilityReporter>();
        static readonly TimeSpan siteDiscoveryPollInterval = TimeSpan.FromSeconds(1);
        const int SiteDiscoveryAttempts = 30;

        readonly CapabilityReport report;
        readonly CentralRoutingSettings settings;
        readonly RoutingTable routingTable;
        readonly RoutingDocumentConfigurationProvider documentProvider;
        readonly IMessageDispatcher dispatcher;
        readonly string controlQueue;
        readonly CancellationTokenSource stopping = new CancellationTokenSource();
        Task heartbeats;

        public CapabilityReporter(CapabilityReport report, CentralRoutingSettings settings, RoutingTable routingTable,
            RoutingDocumentConfigurationProvider documentProvider, IMessageDispatcher dispatcher, string controlQueue)
        {
            this.report = report;
            this.settings = settings;
            this.routingTable = routingTable;
            this.documentProvider = documentProvider;
            this.dispatcher = dispatcher;
            this.controlQueue = controlQueue;
        }

        protected override async Task OnStart(IMessageSession session, CancellationToken cancellationToken = default)
        {
            //Fails the start when the router is unreachable and there is no cached document
            _ = routingTable.Current;

            await SendReport(cancellationToken).ConfigureAwait(false);
            heartbeats = Task.Run(() => SendHeartbeats(stopping.Token), CancellationToken.None);
        }

        protected override async Task OnStop(IMessageSession session, CancellationToken cancellationToken = default)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            if (heartbeats != null)
            {
                await heartbeats.ConfigureAwait(false);
            }

            var stoppingMessage = new InstanceStopping
            {
                EndpointName = report.EndpointName,
                InstanceId = report.InstanceId,
                SentAt = DateTimeOffset.UtcNow
            };
            await Send(ControlMessageTypes.InstanceStopping, RoutingJson.Serialize(stoppingMessage), cancellationToken).ConfigureAwait(false);
        }

        async Task SendHeartbeats(CancellationToken cancellationToken)
        {
            //On first start the router does not know this endpoint's site identity yet. It learns it from the report,
            //so check for a new document a few times instead of waiting for the regular reload.
            for (var attempt = 0; documentProvider != null && routingTable.Current.LocalSite == null && attempt < SiteDiscoveryAttempts; attempt++)
            {
                if (!await Delay(siteDiscoveryPollInterval, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                await TryRefreshDocument(cancellationToken).ConfigureAwait(false);
            }

            while (await Delay(settings.HeartbeatInterval, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await SendReport(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested)
                {
                    log.Warn("Could not send the heartbeat to the router.", e);
                }
            }
        }

        async Task TryRefreshDocument(CancellationToken cancellationToken)
        {
            try
            {
                await documentProvider.Refresh(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                log.Debug("Could not refresh the routing document.", e);
            }
        }

        Task SendReport(CancellationToken cancellationToken)
        {
            report.SentAt = DateTimeOffset.UtcNow;
            report.DocumentRevision = routingTable.Revision;
            return Send(ControlMessageTypes.CapabilityReport, RoutingJson.Serialize(report), cancellationToken);
        }

        Task Send(string messageType, string body, CancellationToken cancellationToken)
        {
            var messageId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, string>
            {
                [Headers.MessageId] = messageId,
                [Headers.ContentType] = "application/json",
                [RoutingHeaders.ControlMessageType] = messageType,
                [RoutingHeaders.ControlMessageVersion] = "1"
            };

            var message = new OutgoingMessage(messageId, headers, Encoding.UTF8.GetBytes(body));
            var operation = new TransportOperation(message, new UnicastAddressTag(controlQueue));
            return dispatcher.Dispatch(new TransportOperations(operation), new TransportTransaction(), cancellationToken);
        }

        static async Task<bool> Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Computes a hash over the handled and published types so that the router can skip unchanged reports.
        /// </summary>
        public static string ComputeHash(IEnumerable<HandledType> handledTypes, IEnumerable<PublishedType> publishedTypes)
        {
            var text = string.Join("\n", handledTypes
                .OrderBy(t => t.Type, StringComparer.Ordinal)
                .Select(t => $"{t.Type}|{t.Kind}|{string.Join(",", t.BaseTypes)}|{string.Join(",", t.Handlers)}")
                .Concat(publishedTypes
                    .OrderBy(t => t.Type, StringComparer.Ordinal)
                    .Select(t => $"publishes {t.Type}|{string.Join(",", t.BaseTypes)}")));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        }
    }
}
