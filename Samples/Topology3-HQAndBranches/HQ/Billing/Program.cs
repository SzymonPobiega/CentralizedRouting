using System;
using System.Threading.Tasks;
using Messages;
using Microsoft.Extensions.Hosting;
using NServiceBus;

//Billing is at HQ. It bills each order and publishes OrderBilled to Shipping.
var host = HqEndpoint.CreateBuilder(args, "Billing", routing => routing.Publishes<OrderBilled>()).Build();
await host.RunAsync();

class OrderPlacedHandler : IHandleMessages<OrderPlaced>
{
    public Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} billed");
        return context.Publish(new OrderBilled { OrderId = message.OrderId, Branch = message.Branch });
    }
}
