using System;
using System.Linq;
using System.Threading.Tasks;
using CentralRouting.Router;
using Microsoft.Extensions.Logging;

/// <summary>
/// Simulates an at-least-once backplane: sends every message going onto the backplane a second time, with 50% probability.
/// Both copies carry the same message ID, as a redelivery would.
/// </summary>
class BackplaneDuplicationBehavior : IForwardingBehavior
{
    readonly ILogger logger;

    public BackplaneDuplicationBehavior(ILogger<BackplaneDuplicationBehavior> logger)
    {
        this.logger = logger;
    }

    public Task Invoke(ForwardingContext context, Func<Task> next)
    {
        var backplaneMessages = context.Messages.Where(m => m.Site == Parts.BackplaneSiteId).ToList();
        if (backplaneMessages.Count > 0 && Random.Shared.Next(2) == 0)
        {
            logger.LogWarning("Sending message {MessageId} over the backplane twice", context.MessageId);
            context.Messages.AddRange(backplaneMessages.Select(m => new ForwardedMessage(m.Site, m.Address, new(m.Headers), m.Body)));
        }
        return next();
    }
}
