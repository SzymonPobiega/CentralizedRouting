using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Transport;

namespace CentralRouting.Router
{
    /// <summary>
    /// A step in the forwarding pipeline. Code before <c>next</c> runs once the router has resolved where the message goes;
    /// code after it runs once every message has been dispatched, before the received message is acknowledged.
    /// Not calling <c>next</c> drops the message.
    /// </summary>
    public interface IForwardingBehavior
    {
        Task Invoke(ForwardingContext context, Func<Task> next);
    }

    /// <summary>
    /// A message the router is about to send. Behaviors can change it, add new ones or remove them.
    /// </summary>
    public class ForwardedMessage
    {
        public ForwardedMessage(string site, string address, Dictionary<string, string> headers, ReadOnlyMemory<byte> body)
        {
            Site = site;
            Address = address;
            Headers = headers;
            Body = body;
        }

        public string Site { get; set; }
        public string Address { get; set; }
        public Dictionary<string, string> Headers { get; }
        public ReadOnlyMemory<byte> Body { get; set; }
    }

    public class ForwardingContext
    {
        readonly Dictionary<string, TransportTransaction> transactions = new Dictionary<string, TransportTransaction>();

        /// <summary>
        /// Created by the router for each message it forwards. Public so that behaviors can be tested on their own.
        /// </summary>
        public ForwardingContext(string messageId, IReadOnlyDictionary<string, string> incomingHeaders, ReadOnlyMemory<byte> body,
            string incomingInterface, string incomingSite, TransportTransaction incomingTransaction, CancellationToken cancellationToken)
        {
            MessageId = messageId;
            IncomingHeaders = incomingHeaders;
            Body = body;
            IncomingInterface = incomingInterface;
            IncomingSite = incomingSite;
            IncomingTransaction = incomingTransaction;
            CancellationToken = cancellationToken;
        }

        public string MessageId { get; }
        public IReadOnlyDictionary<string, string> IncomingHeaders { get; }
        public ReadOnlyMemory<byte> Body { get; }
        public string IncomingInterface { get; }
        public string IncomingSite { get; }

        /// <summary>
        /// Transaction of the receive on the incoming interface.
        /// </summary>
        public TransportTransaction IncomingTransaction { get; }

        public List<ForwardedMessage> Messages { get; } = new List<ForwardedMessage>();

        /// <summary>
        /// State shared between behaviors.
        /// </summary>
        public Dictionary<string, object> Items { get; } = new Dictionary<string, object>();

        public CancellationToken CancellationToken { get; }

        /// <summary>
        /// The transaction used to dispatch the messages for a site. A behavior can put a transport-specific
        /// connection and transaction in it, so the dispatch joins it, and commit it after <c>next</c> returns.
        /// </summary>
        public TransportTransaction GetDispatchTransaction(string site)
        {
            if (!transactions.TryGetValue(site, out var transaction))
            {
                transaction = new TransportTransaction();
                transactions[site] = transaction;
            }
            return transaction;
        }
    }

    class ForwardingPipeline
    {
        readonly IReadOnlyList<IForwardingBehavior> behaviors;
        readonly RouterTopology topology;

        public ForwardingPipeline(IEnumerable<IForwardingBehavior> behaviors, RouterTopology topology)
        {
            this.behaviors = behaviors.ToList();
            this.topology = topology;
        }

        public Task Invoke(ForwardingContext context) => Invoke(context, 0);

        Task Invoke(ForwardingContext context, int index)
        {
            return index < behaviors.Count
                ? behaviors[index].Invoke(context, () => Invoke(context, index + 1))
                : Dispatch(context);
        }

        Task Dispatch(ForwardingContext context)
        {
            var dispatches = context.Messages
                .GroupBy(m => m.Site)
                .Select(site => topology.GetInterface(site.Key).Dispatch(
                    site.Select(m => (m.Address, new OutgoingMessage(context.MessageId, m.Headers, m.Body))),
                    context.GetDispatchTransaction(site.Key),
                    context.CancellationToken));
            return Task.WhenAll(dispatches);
        }
    }
}
