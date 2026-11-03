using System;
using System.Threading.Tasks;
using Messages;
using Microsoft.Extensions.Hosting;
using NServiceBus;

//Sales is on the on-premises site (SQL Server). OrderPlaced reaches Billing through the router,
//and OrderShipped comes back from Shipping through the router.
var host = SampleEndpoint.CreateBuilder(args, "Sales",
        SqlServerSite.CreateTransport(),
        SqlServerSite.Identity,
        //OrderPlaced needs to be declared as published event
        routing => routing.Publishes<OrderPlaced>())
    .Build();
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
