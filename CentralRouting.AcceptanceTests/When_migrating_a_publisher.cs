using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using CentralRouting.Router;
using NServiceBus;
using NUnit.Framework;

namespace CentralRouting.AcceptanceTests
{
    [TestFixture]
    public class When_migrating_a_publisher
    {
        static ConcurrentDictionary<string, TaskCompletionSource<bool>> received;

        [Test]
        public async Task Both_publishers_may_publish_until_the_migration_completes()
        {
            received = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
            await using var environment = await RoutingTestEnvironment.Start(StoreKind.InMemory);
            var type = typeof(OrderPlaced).FullName;

            await environment.StartEndpoint("Sales", RoutingTestEnvironment.SiteA, typeof(SalesOrderPlacedHandler));
            var billing = await environment.StartEndpoint("Billing", RoutingTestEnvironment.SiteB, r => r.Publishes<OrderPlaced>());
            await environment.WaitForPublisher(typeof(OrderPlaced), "Billing");
            await environment.Post($"routes/{type}/subscribers", new SetEndpointRequest("Sales"));

            //The replacement is deployed next to the endpoint it replaces
            var billingV2 = await environment.StartEndpoint("BillingV2", RoutingTestEnvironment.SiteB, r => r.Publishes<OrderPlaced>());
            await environment.WaitForEndpoints(0, "BillingV2");

            var revision = await environment.Post($"routes/{type}/publisher/migration", new SetEndpointRequest("BillingV2"));
            await environment.WaitForEndpoints(revision, "Sales", "Billing", "BillingV2");

            var route = await environment.Get<RouteView>($"routes/{type}");
            Assert.That((route.Publisher, route.PreviousPublisher), Is.EqualTo(("BillingV2", "Billing")));

            await PublishAndReceive(billing, "from-billing");
            await PublishAndReceive(billingV2, "from-billing-v2");

            //Rolling back makes the previous publisher the only one again; then migrate once more
            await environment.Post($"routes/{type}/publisher/migration/rollback", null);
            route = await environment.Get<RouteView>($"routes/{type}");
            Assert.That((route.Publisher, route.PreviousPublisher), Is.EqualTo(("Billing", (string)null)));
            await environment.Post($"routes/{type}/publisher/migration", new SetEndpointRequest("BillingV2"));

            var completed = await environment.Default.Api.DeleteAsync($"api/v1/routes/{type}/publisher/migration");
            completed.EnsureSuccessStatusCode();
            await environment.WaitForEndpoints(await environment.CurrentRevision(), "Billing", "BillingV2");

            var refused = Assert.ThrowsAsync<Exception>(() => billing.Publish(new OrderPlaced { OrderId = "too-late" }));
            Assert.That(refused.Message, Does.Contain("Its only publisher is BillingV2"));
            await PublishAndReceive(billingV2, "after-migration");
        }

        static async Task PublishAndReceive(IMessageSession publisher, string orderId)
        {
            var delivery = received.GetOrAdd(orderId, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            await publisher.Publish(new OrderPlaced { OrderId = orderId });
            await delivery.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        class SalesOrderPlacedHandler : IHandleMessages<OrderPlaced>
        {
            public Task Handle(OrderPlaced message, IMessageHandlerContext context)
            {
                received.GetOrAdd(message.OrderId, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(true);
                return Task.CompletedTask;
            }
        }
    }
}
