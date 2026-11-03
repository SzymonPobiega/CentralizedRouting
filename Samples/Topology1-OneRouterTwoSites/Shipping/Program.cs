using System;
using System.Threading.Tasks;
using Messages;
using Microsoft.Extensions.Hosting;
using NServiceBus;

//Shipping is on the azure site (Azure Service Bus). OrderBilled comes from Billing on the same site, without the router.
var host = SampleEndpoint.CreateBuilder(args, "Shipping", AzureServiceBusSite.CreateTransport(), AzureServiceBusSite.Identity,
    routing => routing.Publishes<OrderShipped>()).Build();
await host.RunAsync();

class OrderBilledHandler : IHandleMessages<OrderBilled>
{
    public Task Handle(OrderBilled message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} shipped");
        return context.Publish(new OrderShipped { OrderId = message.OrderId });
    }
}
