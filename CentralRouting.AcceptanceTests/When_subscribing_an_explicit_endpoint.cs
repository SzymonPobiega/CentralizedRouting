using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CentralRouting.Model;
using CentralRouting.Router;
using NServiceBus;
using NUnit.Framework;

namespace CentralRouting.AcceptanceTests
{
    [TestFixture]
    public class When_subscribing_an_explicit_endpoint
    {
        [Test]
        public async Task The_router_refuses_it_either_way_round()
        {
            await using var environment = await RoutingTestEnvironment.Start(StoreKind.InMemory);

            await environment.StartEndpoint("Sales", RoutingTestEnvironment.SiteA, r => r.Publishes<OrderPlaced>());
            await environment.StartEndpoint("Branch", RoutingTestEnvironment.SiteA, typeof(BranchOrderPlacedHandler));
            await environment.StartEndpoint("Branch", RoutingTestEnvironment.SiteB, typeof(BranchOrderPlacedHandler));
            await environment.WaitForPublisher(typeof(OrderPlaced), "Sales");
            await environment.WaitForEndpoints(0, "Branch");

            var subscribers = $"api/v1/routes/{typeof(OrderPlaced).FullName}/subscribers";
            var policy = "api/v1/endpoints/Branch/address-policy";

            //Each site of an Explicit endpoint is a separate destination, which an event can't address
            await environment.Put("endpoints/Branch/address-policy", new SetAddressPolicyRequest(AddressPolicy.Explicit));
            var subscribe = await environment.Default.Api.PostAsJsonAsync(subscribers, new SetEndpointRequest("Branch"), RoutingJson.Options);
            Assert.That(subscribe.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await subscribe.Content.ReadAsStringAsync(), Does.Contain("can't subscribe to events"));

            //A subscriber can't become Explicit either
            await environment.Put("endpoints/Branch/address-policy", new SetAddressPolicyRequest(AddressPolicy.Strict));
            await environment.Post($"routes/{typeof(OrderPlaced).FullName}/subscribers", new SetEndpointRequest("Branch"));
            var makeExplicit = await environment.Default.Api.PutAsJsonAsync(policy, new SetAddressPolicyRequest(AddressPolicy.Explicit), RoutingJson.Options);
            Assert.That(makeExplicit.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await makeExplicit.Content.ReadAsStringAsync(), Does.Contain(typeof(OrderPlaced).FullName));
        }

        class BranchOrderPlacedHandler : IHandleMessages<OrderPlaced>
        {
            public Task Handle(OrderPlaced message, IMessageHandlerContext context) => Task.CompletedTask;
        }
    }
}
