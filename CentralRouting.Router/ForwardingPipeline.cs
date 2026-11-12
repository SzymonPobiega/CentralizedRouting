using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NServiceBus.Transport;

namespace CentralRouting.Router
{
    /// <summary>
    /// A step in the forwarding pipeline. It runs once per destination site of a received message, with the messages for that
    /// site, so that a site that fails affects only its own messages. Code before <c>next</c> runs once the router has resolved
    /// where the messages go; code after it runs once they have been dispatched. Throwing fails the site: its messages are
    /// retried and, if the site still fails, held and sent later, running through the behaviors again.
    /// Not calling <c>next</c> drops the messages.
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

        /// <summary>
        /// The context for one destination site's run of the behaviors, with copies of its messages.
        /// </summary>
        internal ForwardingContext ForSite(IEnumerable<ForwardedMessage> messages)
        {
            var context = new ForwardingContext(MessageId, IncomingHeaders, Body, IncomingInterface, IncomingSite, IncomingTransaction, CancellationToken);
            context.Messages.AddRange(messages.Select(m => new ForwardedMessage(m.Site, m.Address, new Dictionary<string, string>(m.Headers), m.Body)));
            return context;
        }
    }

    /// <summary>
    /// Runs the behaviors and dispatches, once per destination site, so that a site that fails or is down affects only its own copies.
    /// Copies for such a site move to a holding queue on the incoming site, from which <see cref="ForwardHeld"/> sends them later.
    /// </summary>
    class ForwardingPipeline
    {
        /// <summary>
        /// Dispatch attempts for a site within one receive, before its copies are held.
        /// </summary>
        const int attemptsPerSite = 3;
        static readonly TimeSpan attemptDelay = TimeSpan.FromMilliseconds(200);

        readonly IReadOnlyList<IForwardingBehavior> behaviors;
        readonly RouterTopology topology;
        readonly SiteAvailability availability;
        readonly ILogger logger;

        public ForwardingPipeline(IEnumerable<IForwardingBehavior> behaviors, RouterTopology topology, SiteAvailability availability, ILogger<ForwardingPipeline> logger)
        {
            this.behaviors = behaviors.ToList();
            this.topology = topology;
            this.availability = availability;
            this.logger = logger;
        }

        /// <summary>
        /// Forwards a received message: dispatches its copies for each site, and holds those for sites that are down.
        /// Completes once every copy is dispatched or held.
        /// </summary>
        public async Task Forward(ForwardingContext context, RouterInterface incoming)
        {
            var sites = context.Messages.GroupBy(m => m.Site).ToList();
            var undelivered = await Task.WhenAll(sites.Select(site => ForwardToSite(context, incoming, site.Key, site.ToList()))).ConfigureAwait(false);

            var held = undelivered.Where(u => u != null).SelectMany(u => u).ToList();
            if (held.Count > 0)
            {
                //In the receive's transaction: the copies are held, and the received message completes, together where the transport allows
                await incoming.Hold(held, context.MessageId, context.IncomingTransaction, context.CancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sends a held message from the holding queue for <paramref name="site"/>. Throws when the site still can't take it.
        /// </summary>
        public Task ForwardHeld(ForwardingContext context) => Invoke(context, 0);

        /// <summary>
        /// Returns the copies to hold, or null when they were dispatched.
        /// </summary>
        async Task<IReadOnlyList<ForwardedMessage>> ForwardToSite(ForwardingContext context, RouterInterface incoming, string site, List<ForwardedMessage> messages)
        {
            //A message can't be held on the site that has to take it
            if (site == incoming.SiteId)
            {
                await Invoke(context.ForSite(messages), 0).ConfigureAwait(false);
                return null;
            }

            if (!availability.IsConnected(site) || availability.IsUnavailable(site))
            {
                return messages;
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    //A fresh copy for each attempt: behaviors may have changed the previous one
                    await Invoke(context.ForSite(messages), 0).ConfigureAwait(false);
                    return null;
                }
                catch (Exception e) when (e is not UnforwardableMessageException && !(e is OperationCanceledException && context.CancellationToken.IsCancellationRequested))
                {
                    if (attempt < attemptsPerSite)
                    {
                        await Task.Delay(attemptDelay * attempt, context.CancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    logger.LogDebug(e, "Holding message {MessageId} for site {Site}", context.MessageId, site);
                    //Marks the site as down when it doesn't take a probe either, so its next copies are held without trying
                    await availability.IsReachable(site, e, context.CancellationToken).ConfigureAwait(false);
                    return messages;
                }
            }
        }

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
                .Select(site => Dispatch(context, site.Key, site));
            return Task.WhenAll(dispatches);
        }

        /// <summary>
        /// A failure names the site, so that the router can tell whether the site is down.
        /// </summary>
        async Task Dispatch(ForwardingContext context, string site, IEnumerable<ForwardedMessage> messages)
        {
            var routerInterface = topology.GetInterface(site);
            try
            {
                await routerInterface.Dispatch(
                    messages.Select(m => (m.Address, new OutgoingMessage(context.MessageId, m.Headers, m.Body))),
                    context.GetDispatchTransaction(site),
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not SiteDispatchException && !(e is OperationCanceledException && context.CancellationToken.IsCancellationRequested))
            {
                throw new SiteDispatchException(site, $"Dispatching to site '{site}' failed: {e.Message}", e);
            }
        }
    }
}
