using System;
using System.Threading.Tasks;
using CentralRouting.Model;
using NServiceBus.Pipeline;
using NServiceBus.Transport;

namespace CentralRouting.NServiceBus
{
    /// <summary>
    /// A message that crossed sites carries the original sender's site and queue. A reply to it goes to the router,
    /// which uses them to deliver the reply.
    /// </summary>
    class ReplyToPropagationBehavior : Behavior<IOutgoingReplyContext>
    {
        public override Task Invoke(IOutgoingReplyContext context, Func<Task> next)
        {
            if (context.Extensions.TryGet(out IncomingMessage incoming)
                && incoming.Headers.TryGetValue(RoutingHeaders.ReplyToSite, out var site)
                && incoming.Headers.TryGetValue(RoutingHeaders.ReplyToQueue, out var queue))
            {
                context.Headers[RoutingHeaders.ReplyToSite] = site;
                context.Headers[RoutingHeaders.ReplyToQueue] = queue;
            }
            return next();
        }
    }

    class SubscribeNotSupportedBehavior : Behavior<ISubscribeContext>
    {
        public override Task Invoke(ISubscribeContext context, Func<Task> next)
        {
            throw new InvalidOperationException("Subscriptions are managed by the router. Add the subscriber through the router's API.");
        }
    }

    class UnsubscribeNotSupportedBehavior : Behavior<IUnsubscribeContext>
    {
        public override Task Invoke(IUnsubscribeContext context, Func<Task> next)
        {
            throw new InvalidOperationException("Subscriptions are managed by the router. Remove the subscriber through the router's API.");
        }
    }
}
