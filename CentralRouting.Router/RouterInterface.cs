using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CentralRouting.Model;
using Microsoft.Extensions.Logging;
using NServiceBus;
using NServiceBus.Routing;
using NServiceBus.Transport;

namespace CentralRouting.Router
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

    delegate Task InterfaceMessageHandler(RouterInterface routerInterface, MessageContext message, CancellationToken cancellationToken);

    /// <summary>
    /// A router's connection to one site: a raw transport with the router's queue and control queue.
    /// </summary>
    class RouterInterface
    {
        readonly string routerName;
        readonly int immediateRetries;
        readonly ILogger logger;
        TransportInfrastructure infrastructure;

        public RouterInterface(string routerName, InterfaceConfiguration configuration, int immediateRetries, ILogger logger)
        {
            this.routerName = routerName;
            this.immediateRetries = immediateRetries;
            this.logger = logger;
            Configuration = configuration;
        }

        public InterfaceConfiguration Configuration { get; }
        public string Name => Configuration.Name;
        public string SiteId => Configuration.Site;
        public string RouterAddress { get; private set; }
        public string ControlAddress { get; private set; }

        public async Task Initialize(InterfaceMessageHandler onMessage, InterfaceMessageHandler onControlMessage, CancellationToken cancellationToken)
        {
            var hostSettings = new HostSettings(routerName, $"{routerName} ({Name})", new StartupDiagnosticEntries(),
                (message, exception, _) => logger.LogCritical(exception, "Critical error on interface {Interface}: {Message}", Name, message),
                true, null);

            var receivers = new[]
            {
                new ReceiveSettings("Main", new QueueAddress(routerName), false, false, Configuration.ErrorQueue),
                new ReceiveSettings("Control", new QueueAddress($"{routerName}.Control"), false, false, Configuration.ErrorQueue)
            };

            infrastructure = await Configuration.Transport.Initialize(hostSettings, receivers, new[] { Configuration.ErrorQueue }, cancellationToken)
                .ConfigureAwait(false);

            RouterAddress = infrastructure.ToTransportAddress(receivers[0].ReceiveAddress);
            ControlAddress = infrastructure.ToTransportAddress(receivers[1].ReceiveAddress);

            await infrastructure.Receivers["Main"].Initialize(PushRuntimeSettings.Default,
                (context, token) => onMessage(this, context, token), OnError, cancellationToken).ConfigureAwait(false);
            await infrastructure.Receivers["Control"].Initialize(PushRuntimeSettings.Default,
                (context, token) => onControlMessage(this, context, token), OnError, cancellationToken).ConfigureAwait(false);
        }

        public async Task StartReceive(CancellationToken cancellationToken)
        {
            foreach (var receiver in infrastructure.Receivers.Values)
            {
                await receiver.StartReceive(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task Stop(CancellationToken cancellationToken)
        {
            if (infrastructure == null)
            {
                return;
            }

            foreach (var receiver in infrastructure.Receivers.Values)
            {
                await receiver.StopReceive(cancellationToken).ConfigureAwait(false);
            }
            await infrastructure.Shutdown(cancellationToken).ConfigureAwait(false);
        }

        public string ToTransportAddress(string queue) => infrastructure.ToTransportAddress(new QueueAddress(queue));

        /// <summary>
        /// Receive and dispatch happen on different transports and cannot share the receive transaction,
        /// so the dispatch gets its own, which forwarding behaviors can fill.
        /// </summary>
        public Task Dispatch(IEnumerable<(string Address, OutgoingMessage Message)> messages, TransportTransaction transaction, CancellationToken cancellationToken)
        {
            var operations = new List<TransportOperation>();
            foreach (var (address, message) in messages)
            {
                operations.Add(new TransportOperation(message, new UnicastAddressTag(address)));
            }

            return infrastructure.Dispatcher.Dispatch(new TransportOperations(operations.ToArray()), transaction, cancellationToken);
        }

        async Task<ErrorHandleResult> OnError(ErrorContext context, CancellationToken cancellationToken)
        {
            if (context.Exception is not UnforwardableMessageException && context.ImmediateProcessingFailures <= immediateRetries)
            {
                return ErrorHandleResult.RetryRequired;
            }

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
            await infrastructure.Dispatcher.Dispatch(new TransportOperations(operation), context.TransportTransaction, cancellationToken)
                .ConfigureAwait(false);

            return ErrorHandleResult.Handled;
        }
    }
}
