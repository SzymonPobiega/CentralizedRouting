using System;
using System.Reflection;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Model;
using NServiceBus;
using NServiceBus.Extensibility;
using NServiceBus.Pipeline;
using NServiceBus.Routing;
using NServiceBus.Transport;

namespace NServiceBus.Community.Routing
{
    /// <summary>
    /// Picks the destination of a command from the routing document. Sends with an explicit routing option
    /// (SendLocal, SetDestination, RouteToThisInstance, RouteToSpecificInstance) go where the option says.
    /// </summary>
    class SendRoutingConnector : StageConnector<IOutgoingSendContext, IOutgoingLogicalMessageContext>
    {
        readonly RoutingTable routingTable;
        readonly ReceiveAddresses receiveAddresses;
        readonly ITransportAddressResolver addressResolver;
        readonly string endpointName;

        public SendRoutingConnector(RoutingTable routingTable, ReceiveAddresses receiveAddresses, ITransportAddressResolver addressResolver, string endpointName)
        {
            this.routingTable = routingTable;
            this.receiveAddresses = receiveAddresses;
            this.addressResolver = addressResolver;
            this.endpointName = endpointName;
        }

        public override Task Invoke(IOutgoingSendContext context, Func<IOutgoingLogicalMessageContext, Task> stage)
        {
            var address = SendRouteOptions.TryGetAddress(context, receiveAddresses, addressResolver, endpointName)
                          ?? RouteCentrally(context);

            context.Headers[Headers.MessageIntent] = nameof(MessageIntent.Send);
            return stage(this.CreateOutgoingLogicalMessageContext(context.Message, new[] { new UnicastRoutingStrategy(address) }, context));
        }

        string RouteCentrally(IOutgoingSendContext context)
        {
            var table = routingTable.Current;
            var messageType = context.Message.MessageType.FullName;
            context.GetOperationProperties().TryGet(out RouteToSiteOption routeToSite);

            var destination = table.Resolver.ResolveCommand(messageType, table.LocalSite, routeToSite?.Site)
                              ?? throw new RoutingException($"No route for {messageType}. Assign a destination with the router's API.");

            var address = table.GetAddress(destination, out var viaRouter);
            if (viaRouter)
            {
                context.Headers[RoutingHeaders.DestinationEndpoint] = destination.Endpoint;
                context.Headers[RoutingHeaders.DestinationSite] = destination.Site;
            }
            return address;
        }
    }

    /// <summary>
    /// Reads the routing option set on SendOptions (SendLocal, SetDestination, ...). The option type is not public,
    /// so it is read through reflection.
    /// </summary>
    static class SendRouteOptions
    {
        static readonly Type stateType = typeof(SendOptions).Assembly.GetType("NServiceBus.UnicastSendRouter+State", true);
        static readonly PropertyInfo optionProperty = stateType.GetProperty("Option");
        static readonly PropertyInfo explicitDestinationProperty = stateType.GetProperty("ExplicitDestination");
        static readonly PropertyInfo specificInstanceProperty = stateType.GetProperty("SpecificInstance");
        static readonly MethodInfo tryGet = typeof(IReadOnlyContextBag).GetMethod(nameof(IReadOnlyContextBag.TryGet), 1, new[] { Type.MakeGenericMethodParameter(0).MakeByRefType() })
            .MakeGenericMethod(stateType);

        public static string TryGetAddress(IOutgoingSendContext context, ReceiveAddresses receiveAddresses, ITransportAddressResolver addressResolver, string endpointName)
        {
            var arguments = new object[] { null };
            if (!(bool)tryGet.Invoke(context.GetOperationProperties(), arguments))
            {
                return null;
            }

            var state = arguments[0];
            return optionProperty.GetValue(state).ToString() switch
            {
                "ExplicitDestination" => (string)explicitDestinationProperty.GetValue(state),
                "RouteToAnyInstanceOfThisEndpoint" => receiveAddresses.MainReceiveAddress,
                "RouteToThisInstance" => receiveAddresses.InstanceReceiveAddress
                                         ?? throw new InvalidOperationException("This endpoint instance is not uniquely addressable."),
                "RouteToSpecificInstance" => addressResolver.ToTransportAddress(
                    new QueueAddress(endpointName, (string)specificInstanceProperty.GetValue(state), null, null)),
                _ => null
            };
        }
    }
}
