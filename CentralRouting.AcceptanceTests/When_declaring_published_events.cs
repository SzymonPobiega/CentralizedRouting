using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CentralRouting.Router;
using NServiceBus;
using NUnit.Framework;

namespace CentralRouting.AcceptanceTests
{
    [TestFixture]
    public class When_declaring_published_events
    {
        [Test]
        public async Task The_only_declaring_endpoint_becomes_the_publisher_and_others_cannot_publish()
        {
            await using var environment = await RoutingTestEnvironment.Start(StoreKind.InMemory);

            var sales = await environment.StartEndpoint("Sales", RoutingTestEnvironment.SiteA);
            await environment.StartEndpoint("Billing", RoutingTestEnvironment.SiteB, r => r.Publishes<OrderPlaced>());
            await environment.WaitForPublisher(typeof(OrderPlaced), "Billing");

            var undeclared = Assert.ThrowsAsync<InvalidOperationException>(() => sales.Publish(new OrderPlaced { OrderId = "order-1" }));
            Assert.That(undeclared.Message, Does.Contain("without declaring it"));

            //A second endpoint declaring the event does not take it over
            var shipping = await environment.StartEndpoint("Shipping", RoutingTestEnvironment.SiteB, r => r.Publishes<OrderPlaced>());
            await RoutingTestEnvironment.WaitUntil(async () =>
            {
                var types = await environment.Get<List<MessageTypeView>>("message-types");
                return types.Single(t => t.MessageType == typeof(OrderPlaced).FullName).DeclaredPublishers.Count == 2;
            }, "the router to learn that Shipping declares OrderPlaced too");

            var route = await environment.Get<RouteView>($"routes/{typeof(OrderPlaced).FullName}");
            Assert.That(route.Publisher, Is.EqualTo("Billing"));
            Assert.That(route.Validation.Findings.Select(f => f.Code), Does.Contain("PublisherConflict"));

            await environment.WaitForEndpoints(await environment.CurrentRevision(), "Shipping");
            var notThePublisher = Assert.ThrowsAsync<Exception>(() => shipping.Publish(new OrderPlaced { OrderId = "order-2" }));
            Assert.That(notThePublisher.Message, Does.Contain("Its only publisher is Billing"));
        }
    }
}
