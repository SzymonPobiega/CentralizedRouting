using System;
using System.Threading.Tasks;
using Messages;
using NServiceBus;

class ShipOrderHandler : IHandleMessages<ShipOrder>
{
    readonly Branch branch;

    public ShipOrderHandler(Branch branch)
    {
        this.branch = branch;
    }

    public async Task Handle(ShipOrder message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Packed order {message.OrderId} in branch {branch.Name}, replenishing the stock");
        //Every branch has a Backorder service; the branch router's PreferLocal policy keeps Replenish on the branch's own site
        await context.Send(new Replenish { OrderId = message.OrderId, Branch = branch.Name });
        await context.Reply(new ShipOrderResponse { OrderId = message.OrderId, Branch = branch.Name });
    }
}
