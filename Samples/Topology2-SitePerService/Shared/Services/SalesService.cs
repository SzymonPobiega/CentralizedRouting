using System;
using System.Threading.Tasks;
using NServiceBus.Community.Routing;
using Messages;
using NServiceBus;

static class SalesService
{
    public static void Configure(CentralRoutingSettings routing) => routing.Publishes<OrderPlaced>();
}

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
