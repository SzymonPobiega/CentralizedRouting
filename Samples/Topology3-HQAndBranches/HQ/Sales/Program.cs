using System;
using System.Threading.Tasks;
using Messages;
using Microsoft.Extensions.Hosting;
using NServiceBus;

//Sales is at HQ. It replies to Website and publishes OrderPlaced to Billing, all on the hq site.
var host = HqEndpoint.CreateBuilder(args, "Sales", routing => routing.Publishes<OrderPlaced>()).Build();
await host.RunAsync();

class PlaceOrderHandler : IHandleMessages<PlaceOrder>
{
    public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} placed, shipped by branch {message.Branch}");
        await context.Reply(new PlaceOrderResponse { OrderId = message.OrderId });
        await context.Publish(new OrderPlaced { OrderId = message.OrderId, Branch = message.Branch });
    }
}

class OrderShippedHandler : IHandleMessages<OrderShipped>
{
    public Task Handle(OrderShipped message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} shipped");
        return Task.CompletedTask;
    }
}
