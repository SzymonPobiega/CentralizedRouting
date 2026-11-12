using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CentralRouting.Router;
using NServiceBus;
using NUnit.Framework;

namespace CentralRouting.AcceptanceTests
{
    [TestFixture]
    public class When_an_event_fans_out_to_an_unreachable_site
    {
        const string SiteC = "site-c";
        static readonly ConcurrentDictionary<string, int> received = new ConcurrentDictionary<string, int>();

        [SetUp]
        public void SetUp() => received.Clear();

        [Test]
        public async Task Subscribers_on_other_sites_get_it_once_and_the_unreachable_site_gets_it_when_back()
        {
            await using var environment = new RoutingTestEnvironment();
            var siteC = new FlakyTransport(environment.Site(SiteC));
            await environment.StartRouter(RoutingTestEnvironment.RouterName, configuration =>
            {
                configuration.InterfaceReconnectInterval = TimeSpan.FromMilliseconds(300);
                configuration.UnavailableSiteRetryInterval = TimeSpan.FromMilliseconds(300);
                configuration.AddInterface("a", environment.Site(RoutingTestEnvironment.SiteA)).Site = RoutingTestEnvironment.SiteA;
                configuration.AddInterface("b", environment.Site(RoutingTestEnvironment.SiteB)).Site = RoutingTestEnvironment.SiteB;
                var c = configuration.AddInterface("c", siteC);
                c.Site = SiteC;
                c.SiteIdentities.Add(Model.SiteIdentity.ForLearningTransport(environment.SiteDirectory(SiteC)));
            });

            var sales = await environment.StartEndpoint("Sales", RoutingTestEnvironment.SiteA, r => r.Publishes<OrderPlaced>());
            await environment.StartEndpoint("Billing", RoutingTestEnvironment.SiteB, typeof(BillingHandler));
            await environment.StartEndpoint("Shipping", SiteC, typeof(ShippingHandler));
            await environment.StartEndpoint("Warehouse", SiteC, typeof(WarehouseHandler));

            await environment.WaitForPublisher(typeof(OrderPlaced), "Sales");
            await environment.Post($"routes/{typeof(OrderPlaced).FullName}/subscribers", new SetEndpointRequest("Billing"));
            await environment.Post($"routes/{typeof(OrderPlaced).FullName}/subscribers", new SetEndpointRequest("Shipping"));
            var revision = await environment.Post($"routes/{typeof(OrderPlaced).FullName}/subscribers", new SetEndpointRequest("Warehouse"));
            await environment.WaitForEndpoints(revision, "Sales", "Billing", "Shipping", "Warehouse");

            siteC.Down = true;
            await sales.Publish(new OrderPlaced { OrderId = "order-1" });

            await RoutingTestEnvironment.WaitUntil(() => Task.FromResult(Count("Billing") == 1), "Billing to get the event");
            await RoutingTestEnvironment.WaitUntil(() => Task.FromResult(HoldingQueueLength(environment) == 1),
                "the copies for site C to be held as one message on site A");
            //Several retry intervals pass while site C is down
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.That(Count("Billing"), Is.EqualTo(1), "Site B's copy is not sent again while site C is down");
            Assert.That(Count("Shipping") + Count("Warehouse"), Is.Zero);

            siteC.Down = false;

            await RoutingTestEnvironment.WaitUntil(() => Task.FromResult(Count("Shipping") == 1 && Count("Warehouse") == 1),
                "both subscribers on site C to get the event once it is back");
            Assert.That(HoldingQueueLength(environment), Is.Zero);
            Assert.That(Count("Billing"), Is.EqualTo(1));
        }

        static int Count(string endpoint) => received.TryGetValue(endpoint, out var count) ? count : 0;

        static int HoldingQueueLength(RoutingTestEnvironment environment)
        {
            var directory = Path.Combine(environment.SiteDirectory(RoutingTestEnvironment.SiteA), $"{RoutingTestEnvironment.RouterName}.Pending.{SiteC}");
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.metadata.txt", SearchOption.AllDirectories).Count() : 0;
        }

        class BillingHandler : IHandleMessages<OrderPlaced>
        {
            public Task Handle(OrderPlaced message, IMessageHandlerContext context)
            {
                received.AddOrUpdate("Billing", 1, (_, count) => count + 1);
                return Task.CompletedTask;
            }
        }

        class ShippingHandler : IHandleMessages<OrderPlaced>
        {
            public Task Handle(OrderPlaced message, IMessageHandlerContext context)
            {
                received.AddOrUpdate("Shipping", 1, (_, count) => count + 1);
                return Task.CompletedTask;
            }
        }

        class WarehouseHandler : IHandleMessages<OrderPlaced>
        {
            public Task Handle(OrderPlaced message, IMessageHandlerContext context)
            {
                received.AddOrUpdate("Warehouse", 1, (_, count) => count + 1);
                return Task.CompletedTask;
            }
        }
    }
}
