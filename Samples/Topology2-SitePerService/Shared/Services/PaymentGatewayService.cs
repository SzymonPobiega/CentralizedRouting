using System;
using System.Threading.Tasks;
using Messages;
using NServiceBus;

class ChargeHandler : IHandleMessages<Charge>
{
    public Task Handle(Charge message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Charged the card for order {message.OrderId}");
        return context.Reply(new ChargeResponse { OrderId = message.OrderId });
    }
}
