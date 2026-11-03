using System;
using System.Threading.Tasks;
using Messages;
using NServiceBus;

class ReplenishHandler : IHandleMessages<Replenish>
{
    readonly Branch branch;

    public ReplenishHandler(Branch branch)
    {
        this.branch = branch;
    }

    public Task Handle(Replenish message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Branch {branch.Name} replenishes the stock used by order {message.OrderId}, requested by the warehouse of branch {message.Branch}");
        return Task.CompletedTask;
    }
}
