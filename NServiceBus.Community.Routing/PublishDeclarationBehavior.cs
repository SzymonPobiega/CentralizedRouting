using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NServiceBus.Pipeline;

namespace NServiceBus.Community.Routing
{
    /// <summary>
    /// Refuses to publish events the endpoint does not declare, so the router always knows who publishes what.
    /// </summary>
    class PublishDeclarationBehavior : Behavior<IOutgoingPublishContext>
    {
        readonly IReadOnlySet<Type> publishedTypes;
        readonly string endpointName;

        public PublishDeclarationBehavior(IReadOnlySet<Type> publishedTypes, string endpointName)
        {
            this.publishedTypes = publishedTypes;
            this.endpointName = endpointName;
        }

        public override Task Invoke(IOutgoingPublishContext context, Func<Task> next)
        {
            var eventType = context.Message.MessageType;
            if (!publishedTypes.Contains(eventType))
            {
                throw new InvalidOperationException($"Endpoint {endpointName} publishes {eventType.FullName} without declaring it. " +
                                                    $"Declare it with UseCentralRouting(...).Publishes<{eventType.Name}>().");
            }
            return next();
        }
    }
}
