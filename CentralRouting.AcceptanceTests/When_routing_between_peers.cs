using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CentralRouting.Router;
using NServiceBus;
using NUnit.Framework;

namespace CentralRouting.AcceptanceTests
{
    /// <summary>
    /// HQ and a branch: the HQ router is attached to the hq site, the branch router to the branch site and to hq.
    /// Each router owns the endpoints on its own site and learns the other's through peering.
    /// </summary>
    [TestFixture]
    public class When_routing_between_peers
    {
        const string HqSite = "hq";
        const string BranchSite = "branch";

        static TaskCompletionSource<string> stockAdjusted;
        static TaskCompletionSource<string> replyReceived;
        static TaskCompletionSource<string> priceListReceived;

        [SetUp]
        public void SetUp()
        {
            stockAdjusted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            replyReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            priceListReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        [TestCase(PeerChannel.Interface)]
        [TestCase(PeerChannel.Http)]
        public async Task Commands_replies_and_granted_events_cross_routers(PeerChannel channel)
        {
            await using var environment = new RoutingTestEnvironment();

            var hq = await environment.StartRouter("HQ", c =>
            {
                c.AddInterface("hq", environment.Site(HqSite)).Site = HqSite;
                if (channel == PeerChannel.Interface)
                {
                    c.AddPeer("Branch", "hq");
                }
                else
                {
                    c.AddInboundPeer("Branch");
                }
            });

            var branch = await environment.StartRouter("Branch", c =>
            {
                c.AddInterface("local", environment.Site(BranchSite)).Site = BranchSite;
                c.AddInterface("hq", environment.Site(HqSite)).Site = HqSite;
                if (channel == PeerChannel.Interface)
                {
                    c.AddPeer("HQ", "hq");
                }
                else
                {
                    c.AddHttpPeer("HQ", hq.Url);
                }
            });

            var orders = await environment.StartEndpoint("Orders", HqSite, hq, r => r.Publishes<PriceListChanged>(), typeof(OrdersStockAdjustedHandler));
            await environment.StartEndpoint("Inventory", BranchSite, branch, typeof(InventoryAdjustStockHandler), typeof(InventoryPriceListHandler));

            //HQ learns Inventory from the branch's snapshot and routes a command to it
            await RoutingTestEnvironment.WaitUntil(async () =>
            {
                var endpoints = await hq.Get<List<EndpointView>>("endpoints");
                return endpoints.Any(e => e.Name == "Inventory" && e.Owner == "Branch" && e.Sites.Any(s => s.SiteId == BranchSite));
            }, "HQ to learn Inventory");

            await hq.Put($"routes/{typeof(AdjustStock).FullName}/destination", new SetEndpointRequest("Inventory"));
            await hq.WaitForPublisher(typeof(PriceListChanged), "Orders");

            //The branch requests the event, HQ grants it once Orders runs the revision that forwards it
            await RoutingTestEnvironment.WaitUntil(async () =>
            {
                var peers = await branch.Get<List<PeerView>>("peers");
                return peers.Single().PublishedEvents.Any(e => e.MessageType == typeof(PriceListChanged).FullName);
            }, "the branch to learn that HQ publishes PriceListChanged");

            var conflict = await branch.Api.PostAsync($"api/v1/routes/{typeof(PriceListChanged).FullName}/subscribers",
                System.Net.Http.Json.JsonContent.Create(new SetEndpointRequest("Inventory")));
            Assert.That((int)conflict.StatusCode, Is.EqualTo(409), "Subscribing before the grant is refused");

            await branch.Post("subscription-requests", new CreateSubscriptionRequest(typeof(PriceListChanged).FullName));
            await RoutingTestEnvironment.WaitUntil(async () =>
            {
                var requests = await branch.Get<List<SubscriptionRequestView>>("subscription-requests");
                return requests.Any(r => r.Status == SubscriptionRequestStatus.Granted);
            }, "HQ to grant the subscription request");

            var branchRevision = await branch.Post($"routes/{typeof(PriceListChanged).FullName}/subscribers", new SetEndpointRequest("Inventory"));

            await hq.WaitForEndpoints(await hq.CurrentRevision(), "Orders");
            await branch.WaitForEndpoints(branchRevision, "Inventory");

            await orders.Send(new AdjustStock { Sku = "sku-1" });
            var timeout = TimeSpan.FromSeconds(30);
            Assert.That(await stockAdjusted.Task.WaitAsync(timeout), Is.EqualTo("sku-1"), "Command delivered through the branch router");
            Assert.That(await replyReceived.Task.WaitAsync(timeout), Is.EqualTo("sku-1"), "Reply came back to HQ");

            await orders.Publish(new PriceListChanged { Version = "v2" });
            Assert.That(await priceListReceived.Task.WaitAsync(timeout), Is.EqualTo("v2"), "Event forwarded by HQ to the granted branch");
        }

        class OrdersStockAdjustedHandler : IHandleMessages<StockAdjusted>
        {
            public Task Handle(StockAdjusted message, IMessageHandlerContext context)
            {
                replyReceived.TrySetResult(message.Sku);
                return Task.CompletedTask;
            }
        }

        class InventoryAdjustStockHandler : IHandleMessages<AdjustStock>
        {
            public Task Handle(AdjustStock message, IMessageHandlerContext context)
            {
                stockAdjusted.TrySetResult(message.Sku);
                return context.Reply(new StockAdjusted { Sku = message.Sku });
            }
        }

        class InventoryPriceListHandler : IHandleMessages<PriceListChanged>
        {
            public Task Handle(PriceListChanged message, IMessageHandlerContext context)
            {
                priceListReceived.TrySetResult(message.Version);
                return Task.CompletedTask;
            }
        }
    }
}
