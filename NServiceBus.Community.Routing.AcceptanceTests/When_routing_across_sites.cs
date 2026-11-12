using System;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Router;
using NServiceBus;
using NUnit.Framework;

namespace NServiceBus.Community.Routing.AcceptanceTests
{
    [TestFixture]
    public class When_routing_across_sites
    {
        static TaskCompletionSource<string> responseReceived;
        static TaskCompletionSource<string> eventReceivedBySales;
        static TaskCompletionSource<string> eventReceivedByShipping;

        [SetUp]
        public void SetUp()
        {
            responseReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            eventReceivedBySales = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            eventReceivedByShipping = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        [TestCase(StoreKind.InMemory)]
        [TestCase(StoreKind.SqlServer)]
        public async Task Commands_replies_and_events_reach_their_destinations(StoreKind storeKind)
        {
            await using var environment = await RoutingTestEnvironment.Start(storeKind);

            var sales = await environment.StartEndpoint("Sales", RoutingTestEnvironment.SiteA,
                typeof(SalesResponseHandler), typeof(SalesOrderPlacedHandler));
            await environment.StartEndpoint("Billing", RoutingTestEnvironment.SiteB, r => r.Publishes<OrderPlaced>(),
                typeof(BillingPlaceOrderHandler), typeof(BillingChargeCardHandler));
            await environment.StartEndpoint("Shipping", RoutingTestEnvironment.SiteB,
                typeof(ShippingOrderPlacedHandler));

            await environment.Put($"routes/{typeof(PlaceOrder).FullName}/destination", new SetEndpointRequest("Billing"));
            await environment.WaitForPublisher(typeof(OrderPlaced), "Billing");
            await environment.Post($"routes/{typeof(OrderPlaced).FullName}/subscribers", new SetEndpointRequest("Sales"));
            var revision = await environment.Post($"routes/{typeof(OrderPlaced).FullName}/subscribers", new SetEndpointRequest("Shipping"));
            await environment.WaitForEndpoints(revision, "Sales", "Billing", "Shipping");

            var route = await environment.Get<RouteView>($"routes/{typeof(PlaceOrder).FullName}");
            Assert.That(route.Validation.Status, Is.EqualTo(ValidationStatus.Valid), string.Join("; ", route.Validation.Findings));

            await sales.Send(new PlaceOrder { OrderId = "order-1" });

            var timeout = TimeSpan.FromSeconds(30);
            Assert.That(await responseReceived.Task.WaitAsync(timeout), Is.EqualTo("order-1"), "Reply crossed back to site A");
            Assert.That(await eventReceivedBySales.Task.WaitAsync(timeout), Is.EqualTo("order-1"), "Event fanned out by the router to site A");
            Assert.That(await eventReceivedByShipping.Task.WaitAsync(timeout), Is.EqualTo("order-1"), "Event delivered locally on site B");
        }

        class SalesResponseHandler : IHandleMessages<PlaceOrderResponse>
        {
            public Task Handle(PlaceOrderResponse message, IMessageHandlerContext context)
            {
                responseReceived.TrySetResult(message.OrderId);
                return Task.CompletedTask;
            }
        }

        class SalesOrderPlacedHandler : IHandleMessages<OrderPlaced>
        {
            public Task Handle(OrderPlaced message, IMessageHandlerContext context)
            {
                eventReceivedBySales.TrySetResult(message.OrderId);
                return Task.CompletedTask;
            }
        }

        class BillingPlaceOrderHandler : IHandleMessages<PlaceOrder>
        {
            public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
            {
                await context.Reply(new PlaceOrderResponse { OrderId = message.OrderId });
                await context.SendLocal(new ChargeCard { OrderId = message.OrderId });
            }
        }

        class BillingChargeCardHandler : IHandleMessages<ChargeCard>
        {
            public Task Handle(ChargeCard message, IMessageHandlerContext context)
            {
                return context.Publish(new OrderPlaced { OrderId = message.OrderId });
            }
        }

        class ShippingOrderPlacedHandler : IHandleMessages<OrderPlaced>
        {
            public Task Handle(OrderPlaced message, IMessageHandlerContext context)
            {
                eventReceivedByShipping.TrySetResult(message.OrderId);
                return Task.CompletedTask;
            }
        }
    }
}
