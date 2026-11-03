using System;
using System.Threading.Tasks;
using CentralRouting.Model;
using CentralRouting.Router;
using NServiceBus;
using NUnit.Framework;

namespace CentralRouting.AcceptanceTests
{
    [TestFixture]
    public class When_moving_an_endpoint_between_sites
    {
        static TaskCompletionSource<string> handled;

        [Test]
        public async Task NewestSite_switches_to_the_new_site_and_disabling_it_rolls_back()
        {
            await using var environment = await RoutingTestEnvironment.Start(StoreKind.InMemory);

            var sales = await environment.StartEndpoint("Sales", RoutingTestEnvironment.SiteA);
            await environment.StartEndpoint("Billing", RoutingTestEnvironment.SiteA, typeof(LegacyBillingHandler));

            //The policy can be set only once the router knows the endpoint
            await environment.WaitForEndpoints(0, "Billing");
            await environment.Put($"routes/{typeof(PlaceOrder).FullName}/destination", new SetEndpointRequest("Billing"));
            var revision = await environment.Put("endpoints/Billing/address-policy", new SetAddressPolicyRequest(AddressPolicy.NewestSite));
            await environment.WaitForEndpoints(revision, "Sales", "Billing");

            Assert.That(await SendAndGetHandlingSite(sales, "order-1"), Is.EqualTo(RoutingTestEnvironment.SiteA));

            //The new Billing instance reports from site B, which becomes the newest site
            await environment.StartEndpoint("Billing", RoutingTestEnvironment.SiteB, typeof(NewBillingHandler));
            await RoutingTestEnvironment.WaitUntil(async () =>
            {
                var document = await environment.Get<RoutingDocument>("routing-document");
                return document.Endpoints["Billing"].Addresses.ContainsKey(RoutingTestEnvironment.SiteB);
            }, "Billing to switch to site B");
            await environment.WaitForEndpoints(await environment.CurrentRevision(), "Sales", "Billing");

            Assert.That(await SendAndGetHandlingSite(sales, "order-2"), Is.EqualTo(RoutingTestEnvironment.SiteB));

            //Rollback: disable the new instance
            revision = await environment.Put($"endpoints/Billing/instances/Billing-{RoutingTestEnvironment.SiteB}/disabled", new SetDisabledRequest(true));
            await environment.WaitForEndpoints(revision, "Sales");

            Assert.That(await SendAndGetHandlingSite(sales, "order-3"), Is.EqualTo(RoutingTestEnvironment.SiteA));
        }

        static async Task<string> SendAndGetHandlingSite(IMessageSession sales, string orderId)
        {
            handled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await sales.Send(new PlaceOrder { OrderId = orderId });
            return await handled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        class LegacyBillingHandler : IHandleMessages<PlaceOrder>
        {
            public Task Handle(PlaceOrder message, IMessageHandlerContext context)
            {
                handled.TrySetResult(RoutingTestEnvironment.SiteA);
                return Task.CompletedTask;
            }
        }

        class NewBillingHandler : IHandleMessages<PlaceOrder>
        {
            public Task Handle(PlaceOrder message, IMessageHandlerContext context)
            {
                handled.TrySetResult(RoutingTestEnvironment.SiteB);
                return Task.CompletedTask;
            }
        }
    }
}
