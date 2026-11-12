using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CentralRouting.Router;
using NServiceBus;
using NUnit.Framework;

namespace CentralRouting.AcceptanceTests
{
    [TestFixture]
    public class When_a_site_is_unreachable
    {
        static TaskCompletionSource<string> orderReceived;
        static TaskCompletionSource<string> responseReceived;

        [SetUp]
        public void SetUp()
        {
            orderReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            responseReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        [Test]
        public async Task The_router_starts_without_it_and_attaches_once_it_is_back()
        {
            await using var environment = new RoutingTestEnvironment();
            var siteB = new FlakyTransport(environment.Site(RoutingTestEnvironment.SiteB), down: true);
            await StartRouter(environment, siteB);

            Assert.That(await SiteStatus(environment, RoutingTestEnvironment.SiteB), Is.EqualTo(Router.SiteStatus.Connecting));
            Assert.That(await SiteStatus(environment, RoutingTestEnvironment.SiteA), Is.EqualTo(Router.SiteStatus.Connected));

            var sales = await environment.StartEndpoint("Sales", RoutingTestEnvironment.SiteA, typeof(SalesResponseHandler));
            await environment.StartEndpoint("Billing", RoutingTestEnvironment.SiteB, typeof(BillingPlaceOrderHandler));

            siteB.Down = false;
            await RoutingTestEnvironment.WaitUntil(async () => await SiteStatus(environment, RoutingTestEnvironment.SiteB) == Router.SiteStatus.Connected,
                "the router to connect to site B");
            Assert.That(siteB.ConnectAttempts, Is.GreaterThan(1), "The router kept trying");

            //Billing's report waited in the router's control queue on site B
            await environment.WaitForEndpoints(0, "Billing");
            var revision = await environment.Put($"routes/{typeof(PlaceOrder).FullName}/destination", new SetEndpointRequest("Billing"));
            await environment.WaitForEndpoints(revision, "Sales", "Billing");

            await sales.Send(new PlaceOrder { OrderId = "order-1" });

            Assert.That(await orderReceived.Task.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo("order-1"));
            Assert.That(await responseReceived.Task.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo("order-1"));
        }

        [Test]
        public async Task Messages_for_it_wait_in_a_holding_queue_until_it_is_back()
        {
            await using var environment = new RoutingTestEnvironment();
            var siteB = new FlakyTransport(environment.Site(RoutingTestEnvironment.SiteB));
            await StartRouter(environment, siteB);
            var sales = await StartSalesAndBilling(environment);

            siteB.Down = true;
            await sales.Send(new PlaceOrder { OrderId = "order-1" });

            await RoutingTestEnvironment.WaitUntil(async () => await SiteStatus(environment, RoutingTestEnvironment.SiteB) == Router.SiteStatus.Unreachable,
                "the router to find site B unreachable");
            //Several retry intervals pass without the message failing
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.That(orderReceived.Task.IsCompleted, Is.False);
            Assert.That(QueueLength(environment, RoutingTestEnvironment.SiteA, "error"), Is.Zero);
            Assert.That(QueueLength(environment, RoutingTestEnvironment.SiteA, RoutingTestEnvironment.RouterName), Is.Zero, "The received message completed");
            Assert.That(QueueLength(environment, RoutingTestEnvironment.SiteA, $"{RoutingTestEnvironment.RouterName}.Pending.{RoutingTestEnvironment.SiteB}"),
                Is.EqualTo(1), "The copy for site B waits in the holding queue on site A");

            siteB.Down = false;

            Assert.That(await orderReceived.Task.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo("order-1"), "Forwarded once site B is back");
            Assert.That(await responseReceived.Task.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo("order-1"));
            await RoutingTestEnvironment.WaitUntil(async () => await SiteStatus(environment, RoutingTestEnvironment.SiteB) == Router.SiteStatus.Connected,
                "site B to show as connected again");
        }

        [Test]
        public async Task A_message_the_site_rejects_while_it_is_up_goes_to_the_error_queue()
        {
            await using var environment = new RoutingTestEnvironment();
            var siteB = new FlakyTransport(environment.Site(RoutingTestEnvironment.SiteB))
            {
                Rejects = message => Encoding.UTF8.GetString(message.Body.Span).Contains("poison")
            };
            await StartRouter(environment, siteB);
            var sales = await StartSalesAndBilling(environment);

            await sales.Send(new PlaceOrder { OrderId = "poison" });
            await sales.Send(new PlaceOrder { OrderId = "order-1" });

            Assert.That(await orderReceived.Task.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo("order-1"), "Other messages still get through");
            await RoutingTestEnvironment.WaitUntil(() => Task.FromResult(QueueLength(environment, RoutingTestEnvironment.SiteA, "error") == 1),
                "the rejected message to move to the error queue on site A");
            Assert.That(await SiteStatus(environment, RoutingTestEnvironment.SiteB), Is.EqualTo(Router.SiteStatus.Connected));
        }

        static Task StartRouter(RoutingTestEnvironment environment, FlakyTransport siteB)
        {
            return environment.StartRouter(RoutingTestEnvironment.RouterName, configuration =>
            {
                configuration.InterfaceReconnectInterval = TimeSpan.FromMilliseconds(300);
                configuration.UnavailableSiteRetryInterval = TimeSpan.FromMilliseconds(300);
                configuration.AddInterface("a", environment.Site(RoutingTestEnvironment.SiteA)).Site = RoutingTestEnvironment.SiteA;
                var b = configuration.AddInterface("b", siteB);
                b.Site = RoutingTestEnvironment.SiteB;
                b.SiteIdentities.Add(Model.SiteIdentity.ForLearningTransport(environment.SiteDirectory(RoutingTestEnvironment.SiteB)));
            });
        }

        static async Task<IMessageSession> StartSalesAndBilling(RoutingTestEnvironment environment)
        {
            var sales = await environment.StartEndpoint("Sales", RoutingTestEnvironment.SiteA, typeof(SalesResponseHandler));
            await environment.StartEndpoint("Billing", RoutingTestEnvironment.SiteB, typeof(BillingPlaceOrderHandler));
            var revision = await environment.Put($"routes/{typeof(PlaceOrder).FullName}/destination", new SetEndpointRequest("Billing"));
            await environment.WaitForEndpoints(revision, "Sales", "Billing");
            return sales;
        }

        static async Task<SiteStatus> SiteStatus(RoutingTestEnvironment environment, string site)
        {
            var sites = await environment.Get<List<SiteView>>("sites");
            return sites.Single(s => s.SiteId == site).Status;
        }

        static int QueueLength(RoutingTestEnvironment environment, string site, string queue)
        {
            var directory = Path.Combine(environment.SiteDirectory(site), queue);
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.metadata.txt", SearchOption.AllDirectories).Count() : 0;
        }

        class SalesResponseHandler : IHandleMessages<PlaceOrderResponse>
        {
            public Task Handle(PlaceOrderResponse message, IMessageHandlerContext context)
            {
                responseReceived.TrySetResult(message.OrderId);
                return Task.CompletedTask;
            }
        }

        class BillingPlaceOrderHandler : IHandleMessages<PlaceOrder>
        {
            public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
            {
                orderReceived.TrySetResult(message.OrderId);
                await context.Reply(new PlaceOrderResponse { OrderId = message.OrderId });
            }
        }
    }
}
