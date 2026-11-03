using System;
using System.Threading.Tasks;
using Messages;
using Microsoft.Extensions.Hosting;
using NServiceBus;

//Billing is on the azure site (Azure Service Bus). OrderPlaced arrives through the router.
var host = SampleEndpoint.CreateBuilder(args, "Billing", AzureServiceBusSite.CreateTransport(), AzureServiceBusSite.Identity,
    routing => routing.Publishes<OrderBilled>()).Build();
await host.RunAsync();

class OrderPlacedHandler : IHandleMessages<OrderPlaced>
{
    public Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} billed");
        return context.Publish(new OrderBilled { OrderId = message.OrderId });
    }
}
