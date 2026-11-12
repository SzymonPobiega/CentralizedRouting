using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Model;
using NServiceBus;
using NServiceBus.Extensibility;
using NServiceBus.Logging;
using NServiceBus.Pipeline;
using NServiceBus.Routing;

namespace NServiceBus.Community.Routing
{
    /// <summary>
    /// Publishes by sending a copy to each subscriber on the local site and a single copy to the router
    /// for all subscribers on other sites.
    /// </summary>
    class PublishRoutingConnector : StageConnector<IOutgoingPublishContext, IOutgoingLogicalMessageContext>
    {
        static readonly ILog log = LogManager.GetLogger<PublishRoutingConnector>();

        readonly RoutingTable routingTable;
        readonly string endpointName;

        public PublishRoutingConnector(RoutingTable routingTable, string endpointName)
        {
            this.routingTable = routingTable;
            this.endpointName = endpointName;
        }

        public override async Task Invoke(IOutgoingPublishContext context, Func<IOutgoingLogicalMessageContext, Task> stage)
        {
            var table = routingTable.Current;
            var eventType = context.Message.MessageType;

            var route = table.Resolver.GetEventRoute(eventType.FullName);
            if (route?.Publisher != null && route.Publisher != endpointName && route.PreviousPublisher != endpointName)
            {
                throw new Exception($"Endpoint {endpointName} cannot publish {eventType.FullName}. Its only publisher is {route.Publisher}.");
            }

            var targets = table.Resolver.ResolveEvent(eventType.FullName, table.LocalSite);

            var addresses = new HashSet<string>();
            foreach (var target in targets)
            {
                addresses.Add(table.GetAddress(target, out _));
            }

            //Routers of other sites hold a grant for the event: one copy to this endpoint's router, which forwards it
            if (route?.Forward == true)
            {
                addresses.Add(table.GetRouterAddress());
            }

            if (addresses.Count == 0)
            {
                log.Debug($"No subscribers for {eventType.FullName}.");
                return;
            }

            context.Headers[Headers.MessageIntent] = nameof(MessageIntent.Publish);

            var routingStrategies = addresses.Select(a => (RoutingStrategy)new UnicastRoutingStrategy(a)).ToList();
            await stage(this.CreateOutgoingLogicalMessageContext(context.Message, routingStrategies, context)).ConfigureAwait(false);
        }
    }
}
