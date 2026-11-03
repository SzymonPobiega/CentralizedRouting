using System;
using System.Threading.Tasks;
using Messages;
using Microsoft.Extensions.Hosting;
using NServiceBus;

//Sales replies to Website and publishes OrderPlaced. It subscribes to OrderShipped.
var host = MigratingEndpoint.CreateBuilder(args, "Sales", routing => routing.Publishes<OrderPlaced>()).Build();
await host.RunAsync();

class PlaceOrderHandler : IHandleMessages<PlaceOrder>
{
    public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} placed");
        await context.Reply(new PlaceOrderResponse { OrderId = message.OrderId });
        await context.Publish(new OrderPlaced { OrderId = message.OrderId });
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
