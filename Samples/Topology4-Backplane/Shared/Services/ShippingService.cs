using System;
using System.Threading.Tasks;
using NServiceBus.Community.Routing;
using Messages;
using NServiceBus;

static class ShippingService
{
    public static void Configure(CentralRoutingSettings routing) => routing.Publishes<OrderShipped>();
}

class OrderBilledHandler : IHandleMessages<OrderBilled>
{
    public Task Handle(OrderBilled message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Registering a parcel for order {message.OrderId}");
        return context.Send(new RegisterParcel { OrderId = message.OrderId });
    }
}

class RegisterParcelResponseHandler : IHandleMessages<RegisterParcelResponse>
{
    public Task Handle(RegisterParcelResponse message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} shipped, tracking number {message.TrackingNumber}");
        return context.Publish(new OrderShipped { OrderId = message.OrderId });
    }
}
