using System;
using System.Threading.Tasks;
using NServiceBus.Community.Routing;
using Messages;
using NServiceBus;

static class BillingService
{
    public static void Configure(CentralRoutingSettings routing) => routing.Publishes<OrderBilled>();
}

class OrderPlacedHandler : IHandleMessages<OrderPlaced>
{
    public Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Charging order {message.OrderId}");
        return context.Send(new Charge { OrderId = message.OrderId });
    }
}

class ChargeResponseHandler : IHandleMessages<ChargeResponse>
{
    public Task Handle(ChargeResponse message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} billed");
        return context.Publish(new OrderBilled { OrderId = message.OrderId });
    }
}
