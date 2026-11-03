using System;
using System.Threading.Tasks;
using Messages;
using NServiceBus;

class RegisterParcelHandler : IHandleMessages<RegisterParcel>
{
    public Task Handle(RegisterParcel message, IMessageHandlerContext context)
    {
        var trackingNumber = $"PD{Random.Shared.Next(100000, 999999)}";
        Console.WriteLine($"Registered parcel {trackingNumber} for order {message.OrderId}");
        return context.Reply(new RegisterParcelResponse { OrderId = message.OrderId, TrackingNumber = trackingNumber });
    }
}
