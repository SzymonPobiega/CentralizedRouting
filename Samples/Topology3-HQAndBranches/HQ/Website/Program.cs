using System;
using System.Linq;
using System.Threading.Tasks;
using Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NServiceBus;

//Website is at HQ. It sends PlaceOrder to Sales on the same site; the customer picks the branch that ships the order.
var host = HqEndpoint.CreateBuilder(args, "Website").Build();
await host.StartAsync();
var session = host.Services.GetRequiredService<IMessageSession>();

Console.WriteLine($"Type the branch that ships the order ({string.Join(" or ", Sites.BranchNames)}) and press Enter. Close the window to exit.");
string line;
while ((line = Console.ReadLine()) != null)
{
    var branch = line.Trim().ToUpperInvariant();
    if (!Sites.BranchNames.Contains(branch))
    {
        Console.WriteLine($"Unknown branch '{line.Trim()}'. Type {string.Join(" or ", Sites.BranchNames)}.");
        continue;
    }

    var orderId = Guid.NewGuid().ToString("N")[..8];
    try
    {
        await session.Send(new PlaceOrder { OrderId = orderId, Branch = branch });
        Console.WriteLine($"Sent PlaceOrder {orderId} for branch {branch}");
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
