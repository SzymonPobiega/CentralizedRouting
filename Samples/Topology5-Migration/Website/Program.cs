using System;
using System.Threading.Tasks;
using Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NServiceBus;

//Website sends PlaceOrder to Sales, on whichever site Sales is on.
var host = MigratingEndpoint.CreateBuilder(args, "Website").Build();
await host.StartAsync();
var session = host.Services.GetRequiredService<IMessageSession>();

Console.WriteLine("Press Enter to place an order. Close the window to exit.");
while (Console.ReadLine() != null)
{
    var orderId = Guid.NewGuid().ToString("N")[..8];
    try
    {
        await session.Send(new PlaceOrder { OrderId = orderId });
        Console.WriteLine($"Sent PlaceOrder {orderId}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"Could not send PlaceOrder {orderId}: {e.Message}");
    }
}

await host.StopAsync();

class PlaceOrderResponseHandler : IHandleMessages<PlaceOrderResponse>
{
    public Task Handle(PlaceOrderResponse message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} accepted by Sales");
        return Task.CompletedTask;
    }
}
