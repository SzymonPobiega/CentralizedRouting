using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus;
using NServiceBus.Transport;

namespace NServiceBus.Community.Routing.AcceptanceTests
{
    /// <summary>
    /// The router's view of a site that can go down: while <see cref="Down"/> is set, connecting and dispatching fail.
    /// <see cref="Rejects"/> fails only the matching messages, as a site that is up but refuses a message would.
    /// </summary>
    class FlakyTransport : TransportDefinition
    {
        readonly TransportDefinition inner;

        public FlakyTransport(TransportDefinition inner, bool down = false)
            : base(inner.TransportTransactionMode, inner.SupportsDelayedDelivery, inner.SupportsPublishSubscribe, inner.SupportsTTBR)
        {
            this.inner = inner;
            Down = down;
        }

        public volatile bool Down;
        public Func<OutgoingMessage, bool> Rejects = _ => false;
        public int ConnectAttempts;

        public override async Task<TransportInfrastructure> Initialize(HostSettings hostSettings, ReceiveSettings[] receivers, string[] sendingAddresses,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ConnectAttempts);
            if (Down)
            {
                throw new InvalidOperationException("The site is down.");
            }

            var infrastructure = await inner.Initialize(hostSettings, receivers, sendingAddresses, cancellationToken).ConfigureAwait(false);
            return new Infrastructure(infrastructure, new Dispatcher(infrastructure.Dispatcher, this));
        }

        public override IReadOnlyCollection<TransportTransactionMode> GetSupportedTransactionModes() => inner.GetSupportedTransactionModes();

        class Infrastructure : TransportInfrastructure
        {
            readonly TransportInfrastructure inner;

            public Infrastructure(TransportInfrastructure inner, IMessageDispatcher dispatcher)
            {
                this.inner = inner;
                Dispatcher = dispatcher;
                Receivers = inner.Receivers;
            }

            public override Task Shutdown(CancellationToken cancellationToken = default) => inner.Shutdown(cancellationToken);

            public override string ToTransportAddress(QueueAddress address) => inner.ToTransportAddress(address);
        }

        class Dispatcher : IMessageDispatcher
        {
            readonly IMessageDispatcher inner;
            readonly FlakyTransport transport;

            public Dispatcher(IMessageDispatcher inner, FlakyTransport transport)
            {
                this.inner = inner;
                this.transport = transport;
            }

            public Task Dispatch(TransportOperations outgoingMessages, TransportTransaction transaction, CancellationToken cancellationToken = default)
            {
                if (transport.Down)
                {
                    throw new InvalidOperationException("The site is down.");
                }

                if (outgoingMessages.UnicastTransportOperations.Any(o => transport.Rejects(o.Message)))
                {
                    throw new InvalidOperationException("The site rejected the message.");
                }

                return inner.Dispatch(outgoingMessages, transaction, cancellationToken);
            }
        }
    }
}
