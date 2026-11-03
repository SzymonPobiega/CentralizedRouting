using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CentralRouting.Model;
using NServiceBus;
using NServiceBus.Extensibility;
using NServiceBus.Logging;
using NServiceBus.Pipeline;
using NServiceBus.Routing;
using NServiceBus.Unicast.Messages;

namespace CentralRouting.NServiceBus
{
    /// <summary>
    /// Publishes by sending a copy to each subscriber on the local site and a single copy to the router
    /// for all subscribers on other sites.
    /// </summary>
    class PublishRoutingConnector : StageConnector<IOutgoingPublishContext, IOutgoingLogicalMessageContext>
    {
        static readonly ILog log = LogManager.GetLogger<PublishRoutingConnector>();

        readonly RoutingTable routingTable;
        readonly MessageMetadataRegistry metadataRegistry;
        readonly string endpointName;

        public PublishRoutingConnector(RoutingTable routingTable, MessageMetadataRegistry metadataRegistry, string endpointName)
        {
            this.routingTable = routingTable;
            this.metadataRegistry = metadataRegistry;
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

            var enclosedTypes = metadataRegistry.GetMessageMetadata(eventType).MessageHierarchy.Select(t => t.FullName).ToList();
            var targets = table.Resolver.ResolveEvent(enclosedTypes, table.LocalSite);

            var addresses = new HashSet<string>();
            foreach (var target in targets)
            {
                addresses.Add(table.GetAddress(target, out _));
            }

            //Routers of other sites hold a grant for the event: one copy to this endpoint's router, which forwards it
            if (enclosedTypes.Any(t => table.Resolver.GetEventRoute(t)?.Forward == true))
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
