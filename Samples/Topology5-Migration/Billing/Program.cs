using System;
using System.Threading.Tasks;
using Messages;
using Microsoft.Extensions.Hosting;
using NServiceBus;

//Billing subscribes to OrderPlaced and publishes OrderBilled.
var host = MigratingEndpoint.CreateBuilder(args, "Billing", routing => routing.Publishes<OrderBilled>()).Build();
await host.RunAsync();

class OrderPlacedHandler : IHandleMessages<OrderPlaced>
{
    public Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} billed");
        return context.Publish(new OrderBilled { OrderId = message.OrderId });
    }
}
