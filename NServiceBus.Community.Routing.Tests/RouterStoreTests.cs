using System;
using System.Linq;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Router;
using NServiceBus.Community.Routing.Router.EntityFramework;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace NServiceBus.Community.Routing.Tests
{
    /// <summary>
    /// The contract every store implementation has to fulfill.
    /// </summary>
    [TestFixture("InMemory")]
    [TestFixture("SqlServer")]
    public class RouterStoreTests
    {
        static readonly string sqlServerConnectionString = Environment.GetEnvironmentVariable("CentralRouting_SqlServer_ConnectionString")
                                                           ?? "Server=.;Database=CentralRoutingStoreTests;Integrated Security=true;TrustServerCertificate=true";

        readonly string storeKind;
        IRouterStore store;

        public RouterStoreTests(string storeKind)
        {
            this.storeKind = storeKind;
        }

        [SetUp]
        public async Task SetUp()
        {
            if (storeKind == "InMemory")
            {
                store = new InMemoryRouterStore();
                return;
            }

            var options = new DbContextOptionsBuilder<RouterDbContext>().UseSqlServer(sqlServerConnectionString).Options;
            try
            {
                await using (var db = new RouterDbContext(options))
                {
                    await db.Database.EnsureDeletedAsync();
                }
            }
            catch (SqlException e)
            {
                Assert.Ignore($"SQL Server is not available: {e.Message}");
            }

            store = new EntityFrameworkRouterStore(options);
            await store.Initialize();
        }

        [Test]
        public async Task Saving_with_a_stale_version_fails()
        {
            var endpoint = new EndpointRecord { Name = "Billing" };
            await store.SaveEndpoint(endpoint);

            var first = await store.GetEndpoint("Billing");
            var second = await store.GetEndpoint("Billing");

            first.Instances.Add(new InstanceRecord { InstanceId = "1" });
            await store.SaveEndpoint(first);

            second.Instances.Add(new InstanceRecord { InstanceId = "2" });
            Assert.ThrowsAsync<StoreConcurrencyException>(() => store.SaveEndpoint(second));

            var stored = await store.GetEndpoint("Billing");
            Assert.That(stored.Instances, Has.Count.EqualTo(1));
            Assert.That(stored.Version, Is.EqualTo(first.Version));
        }

        [Test]
        public async Task Inserting_an_existing_record_fails()
        {
            await store.SaveRoute(new RouteRecord { MessageType = "PlaceOrder", Destination = "Billing" });

            Assert.ThrowsAsync<StoreConcurrencyException>(() => store.SaveRoute(new RouteRecord { MessageType = "PlaceOrder", Destination = "Shipping" }));
        }

        [Test]
        public async Task Routes_round_trip_and_can_be_deleted()
        {
            var route = new RouteRecord { MessageType = "OrderPlaced", Kind = RouteKind.Event, Publisher = "Sales", Subscribers = { "Billing", "Shipping" } };
            await store.SaveRoute(route);

            var stored = await store.GetRoute("OrderPlaced");
            Assert.That(stored.Subscribers, Is.EqualTo(new[] { "Billing", "Shipping" }));
            Assert.That(stored.Publisher, Is.EqualTo("Sales"));

            await store.DeleteRoute(stored);
            Assert.That(await store.GetRoute("OrderPlaced"), Is.Null);
        }

        [Test]
        public async Task Revision_increases_monotonically()
        {
            Assert.That(await store.GetRevision(), Is.EqualTo(0));
            Assert.That(await store.IncrementRevision(), Is.EqualTo(1));
            Assert.That(await store.IncrementRevision(), Is.EqualTo(2));
            Assert.That(await store.GetRevision(), Is.EqualTo(2));
        }

        [Test]
        public async Task Peers_round_trip_with_their_snapshot()
        {
            var peer = new PeerRecord
            {
                Name = "Branch",
                LastReceivedAt = DateTimeOffset.UtcNow,
                Snapshot = new PeerSnapshot { Router = "Branch", Revision = 4, PublishedEvents = { new PublishedEvent { MessageType = "StockLow", Publisher = "Inventory" } } }
            };
            await store.SavePeer(peer);

            var stale = (await store.GetPeers()).Single();
            await store.SavePeer(peer);

            Assert.ThrowsAsync<StoreConcurrencyException>(() => store.SavePeer(stale));
            Assert.That((await store.GetPeers()).Single().Snapshot.PublishedEvents.Single().Publisher, Is.EqualTo("Inventory"));
        }

        [Test]
        public async Task Subscription_requests_are_keyed_by_direction_peer_and_type()
        {
            var outgoing = new SubscriptionRequestRecord { MessageType = "PriceChanged", Peer = "HQ", Direction = SubscriptionDirection.Outgoing };
            var incoming = new SubscriptionRequestRecord { MessageType = "PriceChanged", Peer = "HQ", Direction = SubscriptionDirection.Incoming };
            await store.SaveSubscriptionRequest(outgoing);
            await store.SaveSubscriptionRequest(incoming);

            outgoing.Status = SubscriptionRequestStatus.Granted;
            await store.SaveSubscriptionRequest(outgoing);
            await store.DeleteSubscriptionRequest(incoming);

            var stored = (await store.GetSubscriptionRequests()).Single();
            Assert.That(stored.Direction, Is.EqualTo(SubscriptionDirection.Outgoing));
            Assert.That(stored.Status, Is.EqualTo(SubscriptionRequestStatus.Granted));
        }

        [Test]
        public async Task Site_identities_are_added_once()
        {
            var identity = new SiteIdentityRecord { SiteId = "a", Identity = "learning:v1:c:/a", Source = IdentitySource.Learned, FirstSeen = DateTimeOffset.UtcNow };
            await store.AddSiteIdentity(identity);
            await store.AddSiteIdentity(identity);

            Assert.That(await store.GetSiteIdentities(), Has.Count.EqualTo(1));
        }
    }
}
