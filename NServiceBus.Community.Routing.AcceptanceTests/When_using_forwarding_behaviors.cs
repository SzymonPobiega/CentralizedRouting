using System;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Router;
using NServiceBus;
using NUnit.Framework;

namespace NServiceBus.Community.Routing.AcceptanceTests
{
    [TestFixture]
    public class When_using_forwarding_behaviors
    {
        static TaskCompletionSource<string> stampReceived;

        [Test]
        public async Task Behaviors_can_change_forwarded_messages_and_run_after_dispatch()
        {
            stampReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var behavior = new StampingBehavior();

            await using var environment = await RoutingTestEnvironment.Start(StoreKind.InMemory, c => c.AddForwardingBehavior(behavior));

            var sales = await environment.StartEndpoint("Sales", RoutingTestEnvironment.SiteA);
            await environment.StartEndpoint("Billing", RoutingTestEnvironment.SiteB, typeof(BillingHandler));

            var revision = await environment.Put($"routes/{typeof(PlaceOrder).FullName}/destination", new SetEndpointRequest("Billing"));
            await environment.WaitForEndpoints(revision, "Sales", "Billing");

            await sales.Send(new PlaceOrder { OrderId = "order-1" });

            Assert.That(await stampReceived.Task.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo("site-a->site-b"));
            await RoutingTestEnvironment.WaitUntil(() => Task.FromResult(behavior.Dispatched == 1), "the behavior to see the dispatch complete");
        }

        class StampingBehavior : IForwardingBehavior
        {
            int dispatched;

            public int Dispatched => dispatched;

            public async Task Invoke(ForwardingContext context, Func<Task> next)
            {
                foreach (var message in context.Messages)
                {
                    message.Headers["Test.Stamp"] = $"{context.IncomingSite}->{message.Site}";
                }

                await next();
                Interlocked.Increment(ref dispatched);
            }
        }

        class BillingHandler : IHandleMessages<PlaceOrder>
        {
            public Task Handle(PlaceOrder message, IMessageHandlerContext context)
            {
                stampReceived.TrySetResult(context.MessageHeaders.TryGetValue("Test.Stamp", out var stamp) ? stamp : null);
                return Task.CompletedTask;
            }
        }
    }
}
