using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Model;
using Microsoft.Extensions.Logging;
using NServiceBus;
using NServiceBus.Routing;
using NServiceBus.Transport;

namespace NServiceBus.Community.Routing.Router
{
    /// <summary>
    /// Thrown when a message cannot be forwarded. The message is moved to the error queue without retries.
    /// </summary>
    public class UnforwardableMessageException : Exception
    {
        public UnforwardableMessageException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Thrown when messages could not be dispatched to a site. When the site turns out to be unreachable, the message that
    /// was being forwarded stays in its queue and is retried until the site is back; otherwise it fails like any other.
    /// </summary>
    public class SiteDispatchException : Exception
    {
        public SiteDispatchException(string site, string message, Exception innerException = null) : base(message, innerException)
        {
            Site = site;
        }

        public string Site { get; }
    }

    delegate Task InterfaceMessageHandler(RouterInterface routerInterface, MessageContext message, CancellationToken cancellationToken);

    delegate Task HeldMessageHandler(RouterInterface routerInterface, string site, MessageContext message, CancellationToken cancellationToken);

    /// <summary>
    /// A router's connection to one site: a raw transport with the router's queue and control queue.
    /// The interface can exist before it connects, so that the router can start while a site is unreachable.
    /// </summary>
    class RouterInterface
    {
        readonly string routerName;
        readonly RouterConfiguration routerConfiguration;
        readonly SiteAvailability availability;
        readonly ILogger logger;
        volatile TransportInfrastructure infrastructure;
        string routerAddress;

        public RouterInterface(RouterConfiguration routerConfiguration, InterfaceConfiguration configuration, SiteAvailability availability, ILogger logger)
        {
            routerName = routerConfiguration.Name;
            this.routerConfiguration = routerConfiguration;
            this.availability = availability;
            this.logger = logger;
            Configuration = configuration;
        }

        public InterfaceConfiguration Configuration { get; }
        public string Name => Configuration.Name;
        public string SiteId => Configuration.Site;

        /// <summary>
        /// The transport's address of the router's queue once connected, the configured fallback before.
        /// </summary>
        public string RouterAddress => routerAddress ?? Configuration.RouterAddress ?? routerName;

        public bool IsConnected => infrastructure != null;

        /// <summary>
        /// Name of the queue on this interface's site that holds messages for <paramref name="site"/> while it can't take them.
        /// </summary>
        public string HoldingQueue(string site) => $"{routerName}.Pending.{site}";

        /// <summary>
        /// Connects to the site and creates the router's queues: its own, its control queue and a holding queue for each of
        /// <paramref name="otherSites"/>. Receiving starts with <see cref="StartReceive"/>.
        /// Throws when the site is unreachable, leaving the interface disconnected.
        /// </summary>
        public async Task Connect(InterfaceMessageHandler onMessage, InterfaceMessageHandler onControlMessage, HeldMessageHandler onHeldMessage,
            IReadOnlyCollection<string> otherSites, CancellationToken cancellationToken)
        {
            var hostSettings = new HostSettings(routerName, $"{routerName} ({Name})", new StartupDiagnosticEntries(),
                (message, exception, _) => logger.LogCritical(exception, "Critical error on interface {Interface}: {Message}", Name, message),
                true, null);

            var receivers = new[]
            {
                new ReceiveSettings("Main", new QueueAddress(routerName), false, false, Configuration.ErrorQueue),
                new ReceiveSettings("Control", new QueueAddress($"{routerName}.Control"), false, false, Configuration.ErrorQueue)
            }.Concat(otherSites.Select(site => new ReceiveSettings($"Held:{site}", new QueueAddress(HoldingQueue(site)), false, false, Configuration.ErrorQueue)))
            .ToArray();

            TransportInfrastructure created = null;
            try
            {
                created = await Configuration.Transport.Initialize(hostSettings, receivers, new[] { Configuration.ErrorQueue }, cancellationToken)
                    .ConfigureAwait(false);

                await created.Receivers["Main"].Initialize(PushRuntimeSettings.Default,
                    (context, token) => onMessage(this, context, token), OnError, cancellationToken).ConfigureAwait(false);
                await created.Receivers["Control"].Initialize(PushRuntimeSettings.Default,
                    (context, token) => onControlMessage(this, context, token), OnError, cancellationToken).ConfigureAwait(false);
                foreach (var site in otherSites)
                {
                    //One at a time, so held messages leave in the order they were held
                    await created.Receivers[$"Held:{site}"].Initialize(new PushRuntimeSettings(1),
                        (context, token) => onHeldMessage(this, site, context, token), (context, token) => OnHeldError(site, context, token),
                        cancellationToken).ConfigureAwait(false);
                }

                routerAddress = created.ToTransportAddress(receivers[0].ReceiveAddress);
                infrastructure = created;
            }
            catch
            {
                await ShutDown(created).ConfigureAwait(false);
                throw;
            }
        }

        public async Task StartReceive(CancellationToken cancellationToken)
        {
            foreach (var receiver in infrastructure.Receivers.Values)
            {
                await receiver.StartReceive(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Stops receiving and closes the connection to the site.
        /// </summary>
        public async Task Stop(CancellationToken cancellationToken)
        {
            var current = infrastructure;
            if (current == null)
            {
                return;
            }

            infrastructure = null;
            foreach (var receiver in current.Receivers.Values)
            {
                await receiver.StopReceive(cancellationToken).ConfigureAwait(false);
            }
            await current.Shutdown(cancellationToken).ConfigureAwait(false);
        }

        public string ToTransportAddress(string queue) => Connected().ToTransportAddress(new QueueAddress(queue));

        /// <summary>
        /// Receive and dispatch happen on different transports and cannot share the receive transaction,
        /// so the dispatch gets its own, which forwarding behaviors can fill.
        /// </summary>
        public async Task Dispatch(IEnumerable<(string Address, OutgoingMessage Message)> messages, TransportTransaction transaction, CancellationToken cancellationToken)
        {
            var operations = new List<TransportOperation>();
            foreach (var (address, message) in messages)
            {
                operations.Add(new TransportOperation(message, new UnicastAddressTag(address)));
            }

            await Connected().Dispatcher.Dispatch(new TransportOperations(operations.ToArray()), transaction, cancellationToken).ConfigureAwait(false);
            availability.Succeeded(SiteId);
        }

        /// <summary>
        /// Sends a probe to the router's own control queue on the site. Throws when the site is unreachable.
        /// </summary>
        public Task Probe(CancellationToken cancellationToken)
        {
            var messageId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, string>
            {
                [Headers.MessageId] = messageId,
                [RoutingHeaders.ControlMessageType] = ControlMessageTypes.Probe
            };
            var address = ToTransportAddress($"{routerName}.Control");
            return Dispatch(new[] { (address, new OutgoingMessage(messageId, headers, ReadOnlyMemory<byte>.Empty)) }, new TransportTransaction(), cancellationToken);
        }

        /// <summary>
        /// Sends copies that can't be dispatched now to the holding queues for their sites, in the receive's transaction.
        /// Copies for one site that differ only in their destination queue are held as one message, so that a behavior keyed
        /// by message ID, such as deduplication, sees them together again when they are sent.
        /// </summary>
        public Task Hold(IEnumerable<ForwardedMessage> messages, string messageId, TransportTransaction receiveTransaction, CancellationToken cancellationToken)
        {
            var held = messages
                .GroupBy(m => (m.Site, Headers: HeaderKey(m.Headers)))
                .Select(group =>
                {
                    var first = group.First();
                    var headers = new Dictionary<string, string>(first.Headers)
                    {
                        [RoutingHeaders.HeldFor] = JsonSerializer.Serialize(group.Select(m => m.Address).ToArray())
                    };
                    return (ToTransportAddress(HoldingQueue(first.Site)), new OutgoingMessage(messageId, headers, first.Body));
                })
                .ToList();

            return Dispatch(held, receiveTransaction, cancellationToken);
        }

        static string HeaderKey(Dictionary<string, string> headers) =>
            string.Join("\n", headers.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key}={h.Value}"));

        TransportInfrastructure Connected()
        {
            return infrastructure ?? throw new SiteDispatchException(SiteId, $"The router is not connected to site '{SiteId}' yet.");
        }

        async Task<ErrorHandleResult> OnError(ErrorContext context, CancellationToken cancellationToken)
        {
            if (FindSiteFailure(context.Exception) is { } siteFailure)
            {
                var connected = availability.IsConnected(siteFailure.Site);
                if (connected && context.ImmediateProcessingFailures <= routerConfiguration.ImmediateRetries)
                {
                    return ErrorHandleResult.RetryRequired;
                }

                //The message waits in its queue, holding up its receive slot, until the site is back
                if (!connected || !await availability.IsReachable(siteFailure.Site, siteFailure, cancellationToken).ConfigureAwait(false))
                {
                    await Task.Delay(routerConfiguration.UnavailableSiteRetryInterval, cancellationToken).ConfigureAwait(false);
                    return ErrorHandleResult.RetryRequired;
                }
            }
            else if (context.Exception is not UnforwardableMessageException && context.ImmediateProcessingFailures <= routerConfiguration.ImmediateRetries)
            {
                return ErrorHandleResult.RetryRequired;
            }

            return await MoveToErrorQueue(context, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// A held message that can't be sent waits while its site is down, and fails like any other message while the site is up.
        /// </summary>
        async Task<ErrorHandleResult> OnHeldError(string site, ErrorContext context, CancellationToken cancellationToken)
        {
            if (context.Exception is not UnforwardableMessageException)
            {
                var connected = availability.IsConnected(site);
                if (connected && context.ImmediateProcessingFailures <= routerConfiguration.ImmediateRetries)
                {
                    return ErrorHandleResult.RetryRequired;
                }

                if (!connected || !await availability.IsReachable(site, context.Exception, cancellationToken).ConfigureAwait(false))
                {
                    await Task.Delay(routerConfiguration.UnavailableSiteRetryInterval, cancellationToken).ConfigureAwait(false);
                    return ErrorHandleResult.RetryRequired;
                }
            }

            return await MoveToErrorQueue(context, cancellationToken).ConfigureAwait(false);
        }

        async Task<ErrorHandleResult> MoveToErrorQueue(ErrorContext context, CancellationToken cancellationToken)
        {
            logger.LogError(context.Exception, "Moving message {MessageId} received on interface {Interface} to the error queue", context.MessageId, Name);

            var headers = new Dictionary<string, string>(context.Headers)
            {
                [RoutingHeaders.FailureReason] = context.Exception.Message,
                ["NServiceBus.FailedQ"] = context.ReceiveAddress,
                ["NServiceBus.ExceptionInfo.ExceptionType"] = context.Exception.GetType().FullName,
                ["NServiceBus.ExceptionInfo.Message"] = context.Exception.Message,
                ["NServiceBus.ExceptionInfo.StackTrace"] = context.Exception.ToString(),
                ["NServiceBus.TimeOfFailure"] = DateTimeOffsetHelper.ToWireFormattedString(DateTimeOffset.UtcNow)
            };

            var message = new OutgoingMessage(context.MessageId, headers, context.Body);
            var operation = new TransportOperation(message, new UnicastAddressTag(Configuration.ErrorQueue));
            await Connected().Dispatcher.Dispatch(new TransportOperations(operation), context.TransportTransaction, cancellationToken)
                .ConfigureAwait(false);

            return ErrorHandleResult.Handled;
        }

        /// <summary>
        /// Forwarding behaviors may wrap the failure, so look through the inner exceptions.
        /// </summary>
        static SiteDispatchException FindSiteFailure(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is SiteDispatchException siteFailure)
                {
                    return siteFailure;
                }
            }
            return null;
        }

        async Task ShutDown(TransportInfrastructure created)
        {
            if (created == null)
            {
                return;
            }

            try
            {
                await created.Shutdown(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Failed to shut down the transport of interface {Interface} after it failed to connect", Name);
            }
        }
    }
}
