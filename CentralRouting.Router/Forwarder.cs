using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralRouting.Model;
using Microsoft.Extensions.Logging;
using NServiceBus;
using NServiceBus.Transport;

namespace CentralRouting.Router
{
    /// <summary>
    /// Forwards messages that arrive at the router's queue on one site to their destination on another, directly or
    /// through the peer that leads there. The router never re-chooses a site the sender picked.
    /// </summary>
    class Forwarder
    {
        readonly RoutingSnapshotProvider snapshots;
        readonly RouterTopology topology;
        readonly RouterConfiguration configuration;
        readonly ForwardingPipeline pipeline;
        readonly ILogger logger;

        public Forwarder(RoutingSnapshotProvider snapshots, RouterTopology topology, RouterConfiguration configuration, ForwardingPipeline pipeline,
            ILogger<Forwarder> logger)
        {
            this.snapshots = snapshots;
            this.topology = topology;
            this.configuration = configuration;
            this.pipeline = pipeline;
            this.logger = logger;
        }

        public async Task Forward(RouterInterface incoming, MessageContext message, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(message.Headers);

            var hops = headers.TryGetValue(RoutingHeaders.Hops, out var hopsValue) && int.TryParse(hopsValue, out var previousHops) ? previousHops + 1 : 1;
            if (hops > configuration.MaxHops)
            {
                throw new UnforwardableMessageException($"The message exceeded the limit of {configuration.MaxHops} hops.");
            }
            headers[RoutingHeaders.Hops] = hops.ToString(CultureInfo.InvariantCulture);

            var messageId = headers.TryGetValue(Headers.MessageId, out var id) ? id : message.NativeMessageId;
            var context = new ForwardingContext(messageId, message.Headers, message.Body, incoming.Name, incoming.SiteId,
                message.TransportTransaction, cancellationToken);

            try
            {
                Resolve(incoming, headers, context);
            }
            catch (RoutingException e)
            {
                throw new UnforwardableMessageException(e.Message);
            }

            if (context.Messages.Count == 0)
            {
                logger.LogDebug("Message {MessageId} has no destination outside site {Site}", messageId, incoming.SiteId);
                return;
            }

            await pipeline.Invoke(context).ConfigureAwait(false);
        }

        void Resolve(RouterInterface incoming, Dictionary<string, string> headers, ForwardingContext context)
        {
            headers.TryGetValue(Headers.MessageIntent, out var intent);

            if (intent == nameof(MessageIntent.Reply))
            {
                ResolveReply(headers, context);
            }
            else if (headers.ContainsKey(RoutingHeaders.DestinationEndpoint))
            {
                ResolveEndpoint(incoming, headers, context);
            }
            else if (intent == nameof(MessageIntent.Publish))
            {
                ResolveEvent(incoming, headers, context);
            }
            else
            {
                throw new UnforwardableMessageException("The message has no destination endpoint and is neither a reply nor an event.");
            }
        }

        void ResolveReply(Dictionary<string, string> headers, ForwardingContext context)
        {
            if (!headers.TryGetValue(RoutingHeaders.ReplyToSite, out var site) || !headers.TryGetValue(RoutingHeaders.ReplyToQueue, out var queue))
            {
                throw new UnforwardableMessageException($"The reply has no {RoutingHeaders.ReplyToSite} and {RoutingHeaders.ReplyToQueue} headers.");
            }

            var model = snapshots.Current.Model;
            if (model.IsAttached(site))
            {
                headers.Remove(RoutingHeaders.ReplyToSite);
                headers.Remove(RoutingHeaders.ReplyToQueue);
                context.Messages.Add(new ForwardedMessage(site, queue, headers, context.Body));
                return;
            }

            var peerRoute = FindPeerRoute(model, site);
            context.Messages.Add(new ForwardedMessage(peerRoute.SharedSite, peerRoute.PeerAddress, headers, context.Body));
        }

        void ResolveEndpoint(RouterInterface incoming, Dictionary<string, string> headers, ForwardingContext context)
        {
            var snapshot = snapshots.Current;
            var endpoint = headers[RoutingHeaders.DestinationEndpoint];
            if (!headers.TryGetValue(RoutingHeaders.DestinationSite, out var site))
            {
                //Sent by an endpoint that did not pick a site
                site = snapshot.Resolver.ChooseSite(endpoint, incoming.SiteId, null);
                headers[RoutingHeaders.DestinationSite] = site;
            }

            if (snapshot.Model.IsAttached(site))
            {
                var queue = snapshot.Resolver.ChooseQueue(endpoint, site);
                RewriteReplyTo(headers, incoming, topology.GetInterface(site));
                context.Messages.Add(new ForwardedMessage(site, queue, headers, context.Body));
                return;
            }

            var peerRoute = FindPeerRoute(snapshot.Model, site);
            RewriteReplyTo(headers, incoming, topology.GetInterface(peerRoute.SharedSite));
            context.Messages.Add(new ForwardedMessage(peerRoute.SharedSite, peerRoute.PeerAddress, headers, context.Body));
        }

        /// <summary>
        /// An event from a publisher on this router's sites goes to the subscribers on the other sites and, once, to each
        /// peer holding a grant. An event from a peer goes to this router's own subscribers wherever they are.
        /// </summary>
        void ResolveEvent(RouterInterface incoming, Dictionary<string, string> headers, ForwardingContext context)
        {
            var snapshot = snapshots.Current;
            var model = snapshot.Model;
            var eventType = PublishedType(headers);

            var fromPeer = headers.TryGetValue(RoutingHeaders.OriginRouter, out var origin) && origin != configuration.Name;

            var targets = snapshot.Resolver.ResolveEvent(eventType, incoming.SiteId)
                .Where(t => fromPeer || t.Site != incoming.SiteId);

            foreach (var target in targets)
            {
                if (!model.IsAttached(target.Site))
                {
                    logger.LogWarning("Subscriber {Endpoint} of {MessageId} lives on site {Site}, which this router is not attached to",
                        target.Endpoint, context.MessageId, target.Site);
                    continue;
                }

                var siteHeaders = new Dictionary<string, string>(headers);
                RewriteReplyTo(siteHeaders, incoming, topology.GetInterface(target.Site));
                context.Messages.Add(new ForwardedMessage(target.Site, snapshot.Resolver.ChooseQueue(target.Endpoint, target.Site), siteHeaders, context.Body));
            }

            if (fromPeer)
            {
                //Information travels one hop, and so do events
                return;
            }

            foreach (var peer in model.GrantedPeers(eventType))
            {
                if (!model.Peers.TryGetValue(peer, out var peerRecord))
                {
                    continue;
                }

                var shared = peerRecord.Snapshot.Sites.FirstOrDefault(s => model.IsAttached(s.SiteId));
                if (shared == null)
                {
                    logger.LogWarning("Peer {Peer} holds a grant for {MessageId} but shares no site with this router", peer, context.MessageId);
                    continue;
                }

                var peerHeaders = new Dictionary<string, string>(headers)
                {
                    [RoutingHeaders.OriginRouter] = configuration.Name,
                    [RoutingHeaders.Emitter] = configuration.Name
                };
                RewriteReplyTo(peerHeaders, incoming, topology.GetInterface(shared.SiteId));
                context.Messages.Add(new ForwardedMessage(shared.SiteId, shared.RouterAddress, peerHeaders, context.Body));
            }
        }

        static PeerRoute FindPeerRoute(RoutingModel model, string site)
        {
            return model.FindPeerRoute(site)
                   ?? throw new UnforwardableMessageException($"Site '{site}' is neither attached to this router nor reachable through a peer.");
        }

        /// <summary>
        /// Replies must come back through a router, so the router records the original reply address and
        /// replaces it with its own queue on the outgoing site.
        /// </summary>
        static void RewriteReplyTo(Dictionary<string, string> headers, RouterInterface incoming, RouterInterface outgoing)
        {
            if (!headers.TryGetValue(Headers.ReplyToAddress, out var replyTo))
            {
                return;
            }

            if (!headers.ContainsKey(RoutingHeaders.ReplyToSite))
            {
                headers[RoutingHeaders.ReplyToSite] = incoming.SiteId;
                headers[RoutingHeaders.ReplyToQueue] = replyTo;
            }
            headers[Headers.ReplyToAddress] = outgoing.RouterAddress;
        }

        /// <summary>
        /// The type the publisher published, which NServiceBus lists first in the enclosed message types, before its base types.
        /// </summary>
        static string PublishedType(Dictionary<string, string> headers)
        {
            if (!headers.TryGetValue(Headers.EnclosedMessageTypes, out var enclosed) || string.IsNullOrWhiteSpace(enclosed))
            {
                throw new UnforwardableMessageException("The event has no enclosed message types header.");
            }

            return enclosed.Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Split(',')[0].Trim();
        }
    }
}
