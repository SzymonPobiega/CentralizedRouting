using System;
using System.Threading.Tasks;
using NServiceBus.Community.Routing;
using Messages;
using Microsoft.Extensions.Hosting;
using NServiceBus;

//Shipping is at HQ. Every branch has a Warehouse, so it names the branch's site when it sends ShipOrder.
//The message goes straight to that branch router's queue on hq, and the branch router delivers it to its Warehouse.
var host = HqEndpoint.CreateBuilder(args, "Shipping", routing => routing.Publishes<OrderShipped>()).Build();
await host.RunAsync();

class OrderBilledHandler : IHandleMessages<OrderBilled>
{
    public Task Handle(OrderBilled message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Shipping order {message.OrderId} from branch {message.Branch}");
        var options = new SendOptions();
        options.RouteToSite(Sites.BranchSiteId(message.Branch));
        return context.Send(new ShipOrder { OrderId = message.OrderId }, options);
    }
}

class ShipOrderResponseHandler : IHandleMessages<ShipOrderResponse>
{
    public Task Handle(ShipOrderResponse message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} shipped by branch {message.Branch}");
        return context.Publish(new OrderShipped { OrderId = message.OrderId });
    }
}
