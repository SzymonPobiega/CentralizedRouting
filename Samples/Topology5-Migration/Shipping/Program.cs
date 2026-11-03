using System;
using System.Threading.Tasks;
using Messages;
using Microsoft.Extensions.Hosting;
using NServiceBus;

//Shipping subscribes to OrderBilled and publishes OrderShipped.
var host = MigratingEndpoint.CreateBuilder(args, "Shipping", routing => routing.Publishes<OrderShipped>()).Build();
await host.RunAsync();

class OrderBilledHandler : IHandleMessages<OrderBilled>
{
    public Task Handle(OrderBilled message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} shipped");
        return context.Publish(new OrderShipped { OrderId = message.OrderId });
    }
}
