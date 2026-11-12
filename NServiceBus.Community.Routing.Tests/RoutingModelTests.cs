using System;
using System.Collections.Generic;
using System.Linq;
using NServiceBus.Community.Routing.Model;
using NServiceBus.Community.Routing.Router;
using NUnit.Framework;

namespace NServiceBus.Community.Routing.Tests
{
    [TestFixture]
    public class RoutingModelTests
    {
        static readonly DateTimeOffset now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        static readonly TimeSpan expiry = TimeSpan.FromMinutes(15);

        [Test]
        public void Capability_is_the_intersection_over_live_instances()
        {
            var endpoint = Endpoint("Billing",
                Instance("1", "a", "PlaceOrder", "Refund"),
                Instance("2", "a", "PlaceOrder"),
                Instance("3", "a", "Legacy", lastReport: now - TimeSpan.FromHours(1)));

            var model = Model(new[] { endpoint });

            Assert.That(model.GetCapability(endpoint, "a", "PlaceOrder"), Is.EqualTo(CapabilityState.Capable));
            Assert.That(model.GetCapability(endpoint, "a", "Refund"), Is.EqualTo(CapabilityState.Partial));
            Assert.That(model.GetCapability(endpoint, "a", "Legacy"), Is.EqualTo(CapabilityState.Missing), "Stale instances do not count");
            Assert.That(model.GetCapability(endpoint, "b", "PlaceOrder"), Is.EqualTo(CapabilityState.Offline));
        }

        [Test]
        public void Handling_a_base_type_makes_an_endpoint_capable_of_the_derived_type()
        {
            var subscriber = Endpoint("Subscriber", Instance("1", "a", "OrderEvent"));
            var publisher = Endpoint("Other", new InstanceRecord
            {
                InstanceId = "2",
                SiteId = "a",
                LastReportAt = now,
                HandledTypes = { new HandledType { Type = "OrderPlaced", BaseTypes = { "OrderEvent" } } }
            });

            var model = Model(new[] { subscriber, publisher });

            Assert.That(model.GetCapability(subscriber, "a", "OrderPlaced"), Is.EqualTo(CapabilityState.Capable));
        }

        [Test]
        public void Disabled_instances_and_sites_get_no_traffic()
        {
            var endpoint = Endpoint("Billing", Instance("1", "a", "PlaceOrder"), Instance("2", "a", "PlaceOrder"), Instance("3", "b", "PlaceOrder"));
            endpoint.Instances[1].Disabled = true;
            endpoint.Sites.Single(s => s.SiteId == "b").Disabled = true;

            var addresses = Model(new[] { endpoint }).GetPublishedAddresses(endpoint);

            Assert.That(addresses.Keys, Is.EquivalentTo(new[] { "a" }));
            Assert.That(addresses["a"]["Billing"], Is.EqualTo(1));
        }

        [Test]
        public void An_endpoint_without_live_instances_keeps_its_last_known_queues()
        {
            var endpoint = Endpoint("Shipping", Instance("1", "a", "OrderBilled"), Instance("2", "a", "OrderBilled"));
            endpoint.Instances[0].Stopped = true;
            endpoint.Instances[1].LastReportAt = now - TimeSpan.FromHours(1);
            var model = Model(new[] { endpoint });

            Assert.That(model.GetPublishedAddresses(endpoint)["a"]["Shipping"], Is.EqualTo(2), "Messages wait in the queue instead of being lost");
            Assert.That(model.GetCapability(endpoint, "a", "OrderBilled"), Is.EqualTo(CapabilityState.Offline));

            endpoint.Instances.ForEach(i => i.Disabled = true);
            Assert.That(model.GetPublishedAddresses(endpoint), Is.Empty, "Disabled instances get no traffic even then");
        }

        [Test]
        public void NewestSite_switches_only_when_the_new_site_handles_every_routed_type()
        {
            var endpoint = Endpoint("Billing", Instance("1", "old", "PlaceOrder", "Refund"), Instance("2", "new", "PlaceOrder"));
            endpoint.AddressPolicy = AddressPolicy.NewestSite;
            endpoint.Sites.Single(s => s.SiteId == "new").FirstSeen = now;
            var routes = new List<RouteRecord>
            {
                new RouteRecord { MessageType = "PlaceOrder", Kind = RouteKind.Command, Destination = "Billing" },
                new RouteRecord { MessageType = "Refund", Kind = RouteKind.Command, Destination = "Billing" }
            };

            Assert.That(Model(new[] { endpoint }, routes).GetPublishedAddresses(endpoint).Keys, Is.EquivalentTo(new[] { "old" }));

            endpoint.Instances[1].HandledTypes.Add(new HandledType { Type = "Refund" });

            Assert.That(Model(new[] { endpoint }, routes).GetPublishedAddresses(endpoint).Keys, Is.EquivalentTo(new[] { "new" }));
        }

        [Test]
        public void Validation_reports_missing_capability_and_strict_endpoints_on_several_sites()
        {
            var endpoint = Endpoint("Billing", Instance("1", "a", "PlaceOrder"), Instance("2", "b", "PlaceOrder"));
            var model = Model(new[] { endpoint });

            var notCapable = model.Validate(new RouteRecord { MessageType = "Refund", Kind = RouteKind.Command, Destination = "Billing" });
            Assert.That(notCapable.Status, Is.EqualTo(ValidationStatus.Invalid));

            var multipleSites = model.Validate(new RouteRecord { MessageType = "PlaceOrder", Kind = RouteKind.Command, Destination = "Billing" });
            Assert.That(multipleSites.Status, Is.EqualTo(ValidationStatus.Warning));
            Assert.That(multipleSites.Findings.Select(f => f.Code), Is.EqualTo(new[] { "MultipleSites" }));
        }

        [Test]
        public void Document_lists_each_site_with_the_router_queue_leading_to_the_others()
        {
            var document = Model(Array.Empty<EndpointRecord>()).BuildDocument(7);

            Assert.That(document.Revision, Is.EqualTo(7));
            Assert.That(document.Sites["a"].NextHop, Is.EquivalentTo(new Dictionary<string, string> { ["b"] = "Router@a" }));
            Assert.That(document.Sites["b"].NextHop, Is.EquivalentTo(new Dictionary<string, string> { ["a"] = "Router@b" }));
            Assert.That(document.Sites["a"].Identities, Is.EqualTo(new[] { "id-a" }));
        }

        [Test]
        public void Learned_endpoints_are_published_with_the_peer_as_next_hop()
        {
            var peer = new PeerRecord
            {
                Name = "Branch",
                LastReceivedAt = now,
                Snapshot = new PeerSnapshot
                {
                    Router = "Branch",
                    Sites =
                    {
                        new PeerSite { SiteId = "a", RouterAddress = "Branch@a" },
                        new PeerSite { SiteId = "branch", RouterAddress = "Branch@branch" }
                    },
                    Endpoints =
                    {
                        new PeerEndpoint
                        {
                            Name = "Inventory",
                            Policy = AddressPolicy.Strict,
                            Addresses = { ["branch"] = new Dictionary<string, int> { ["Inventory"] = 2 } },
                            Capabilities = { ["branch"] = new Dictionary<string, CapabilityState> { ["AdjustStock"] = CapabilityState.Capable } }
                        }
                    }
                }
            };

            var model = Model(Array.Empty<EndpointRecord>(), peers: new[] { peer });
            var document = model.BuildDocument(1);

            Assert.That(document.Endpoints["Inventory"].Addresses["branch"]["Inventory"], Is.EqualTo(2));
            Assert.That(document.Sites["a"].NextHop["branch"], Is.EqualTo("Branch@a"), "Senders on a shared site go to the peer directly");
            Assert.That(document.Sites["b"].NextHop["branch"], Is.EqualTo("Router@b"), "Senders on other sites go through this router");
            Assert.That(model.FindPeerRoute("branch"), Is.EqualTo(new PeerRoute("Branch", "a", "Branch@a")));

            var validation = model.Validate(new RouteRecord { MessageType = "AdjustStock", Kind = RouteKind.Command, Destination = "Inventory" });
            Assert.That(validation.Status, Is.EqualTo(ValidationStatus.Valid));
        }

        [Test]
        public void An_endpoint_of_a_peer_is_an_invalid_subscriber()
        {
            var sales = Endpoint("Sales", Publishing(Instance("1", "a", "PlaceOrder"), "OrderPlaced"));
            var peer = new PeerRecord
            {
                Name = "BillingRouter",
                LastReceivedAt = now,
                Snapshot = new PeerSnapshot { Router = "BillingRouter", Endpoints = { new PeerEndpoint { Name = "Billing" } } }
            };
            var model = Model(new[] { sales }, peers: new[] { peer });

            var validation = model.Validate(new RouteRecord { MessageType = "OrderPlaced", Kind = RouteKind.Event, Publisher = "Sales", Subscribers = { "Billing" } });

            Assert.That(validation.Status, Is.EqualTo(ValidationStatus.Invalid));
            Assert.That(validation.Findings.Single().Code, Is.EqualTo("PeerSubscriber"));
            Assert.That(validation.Findings.Single().Message, Does.Contain("BillingRouter"));
            Assert.That(model.GetOwningPeers("Sales"), Is.Empty);
        }

        [Test]
        public void An_explicit_endpoint_is_an_invalid_subscriber()
        {
            var sales = Endpoint("Sales", Publishing(Instance("1", "a", "PlaceOrder"), "OrderPlaced"));
            var warehouse = Endpoint("Warehouse", Instance("2", "a", "OrderPlaced"), Instance("3", "b", "OrderPlaced"));
            warehouse.AddressPolicy = AddressPolicy.Explicit;
            var route = new RouteRecord { MessageType = "OrderPlaced", Kind = RouteKind.Event, Publisher = "Sales", Subscribers = { "Warehouse" } };
            var model = Model(new[] { sales, warehouse }, new List<RouteRecord> { route });

            var validation = model.Validate(route);

            Assert.That(validation.Status, Is.EqualTo(ValidationStatus.Invalid));
            Assert.That(validation.Findings.Single().Code, Is.EqualTo("ExplicitSubscriber"));
            Assert.That(model.GetSubscriptions("Warehouse"), Is.EqualTo(new[] { "OrderPlaced" }));
        }

        [Test]
        public void Snapshot_exports_only_what_the_router_owns()
        {
            var owned = Endpoint("Orders", Instance("1", "a", "PlaceOrder"));
            var peer = new PeerRecord
            {
                Name = "Branch",
                LastReceivedAt = now,
                Snapshot = new PeerSnapshot { Router = "Branch", Endpoints = { new PeerEndpoint { Name = "Inventory" } } }
            };
            var requests = new[]
            {
                new SubscriptionRequestRecord { MessageType = "PriceChanged", Peer = "Branch", Direction = SubscriptionDirection.Incoming, Status = SubscriptionRequestStatus.Granted },
                new SubscriptionRequestRecord { MessageType = "StockLow", Peer = "Branch", Direction = SubscriptionDirection.Outgoing, Status = SubscriptionRequestStatus.Requested }
            };
            var routes = new List<RouteRecord> { new RouteRecord { MessageType = "PriceChanged", Kind = RouteKind.Event, Publisher = "Orders" } };

            var model = Model(new[] { owned }, routes, new[] { peer }, requests);
            var snapshot = model.BuildSnapshot(3);

            Assert.That(snapshot.Endpoints.Select(e => e.Name), Is.EqualTo(new[] { "Orders" }));
            Assert.That(snapshot.PublishedEvents.Select(e => e.MessageType), Is.EqualTo(new[] { "PriceChanged" }));
            Assert.That(snapshot.Grants.Select(g => (g.MessageType, g.Router)), Is.EqualTo(new[] { ("PriceChanged", "Branch") }));
            Assert.That(snapshot.Requests.Select(g => (g.MessageType, g.Router)), Is.EqualTo(new[] { ("StockLow", "Branch") }));
            Assert.That(model.BuildDocument(3).Events["PriceChanged"].Forward, Is.True);
        }

        [Test]
        public void An_event_declared_by_exactly_one_endpoint_is_assigned_to_it()
        {
            var billing = Endpoint("Billing", Publishing(Instance("1", "a", "PlaceOrder"), "OrderPlaced", "InvoiceSent"));
            var shipping = Endpoint("Shipping", Publishing(Instance("2", "b", "ShipOrder"), "InvoiceSent"));
            var legacy = Endpoint("Legacy", Publishing(Instance("3", "b", "Other", lastReport: now - TimeSpan.FromHours(1)), "StockLow"));

            var assignments = Model(new[] { billing, shipping, legacy }).GetPublisherAssignments();

            Assert.That(assignments, Is.EqualTo(new[] { ("OrderPlaced", "Billing") }), "Declared twice or only by stale instances: left to the operator");

            var assigned = new List<RouteRecord> { new RouteRecord { MessageType = "OrderPlaced", Kind = RouteKind.Event, Publisher = "Billing" } };
            Assert.That(Model(new[] { billing, shipping }, assigned).GetPublisherAssignments(), Is.Empty);
        }

        [Test]
        public void Events_published_by_a_peer_are_not_assigned_locally()
        {
            var billing = Endpoint("Billing", Publishing(Instance("1", "a", "PlaceOrder"), "PriceChanged"));
            var peer = new PeerRecord
            {
                Name = "HQ",
                LastReceivedAt = now,
                Snapshot = new PeerSnapshot { Router = "HQ", PublishedEvents = { new PublishedEvent { MessageType = "PriceChanged", Publisher = "Pricing" } } }
            };

            Assert.That(Model(new[] { billing }, peers: new[] { peer }).GetPublisherAssignments(), Is.Empty);
        }

        [Test]
        public void Validation_reports_undeclared_and_conflicting_publishers()
        {
            var billing = Endpoint("Billing", Instance("1", "a", "PlaceOrder"));
            var shipping = Endpoint("Shipping", Publishing(Instance("2", "b", "ShipOrder"), "OrderPlaced"));
            var model = Model(new[] { billing, shipping });

            var validation = model.Validate(new RouteRecord { MessageType = "OrderPlaced", Kind = RouteKind.Event, Publisher = "Billing" });

            Assert.That(validation.Status, Is.EqualTo(ValidationStatus.Invalid));
            Assert.That(validation.Findings.Select(f => f.Code), Is.EquivalentTo(new[] { "NotDeclared", "PublisherConflict" }));
            Assert.That(model.GetKnownTypes()["OrderPlaced"], Is.EqualTo(MessageKind.Event), "Declared events are known types");
        }

        [Test]
        public void During_a_migration_both_publishers_are_published_and_the_previous_one_is_no_conflict()
        {
            var billing = Endpoint("Billing", Publishing(Instance("1", "a", "PlaceOrder"), "OrderPlaced"));
            var billingV2 = Endpoint("BillingV2", Publishing(Instance("2", "b", "PlaceOrder"), "OrderPlaced"));
            var route = new RouteRecord
            {
                MessageType = "OrderPlaced",
                Kind = RouteKind.Event,
                Publisher = "BillingV2",
                PreviousPublisher = "Billing",
                MigrationStartedAt = now
            };
            var model = Model(new[] { billing, billingV2 }, new List<RouteRecord> { route });

            var validation = model.Validate(route);
            Assert.That(validation.Findings.Select(f => f.Code), Is.EqualTo(new[] { "PublisherMigration" }));
            Assert.That(validation.Status, Is.EqualTo(ValidationStatus.Warning), "A migration is meant to be temporary");

            var document = model.BuildDocument(1);
            Assert.That((document.Events["OrderPlaced"].Publisher, document.Events["OrderPlaced"].PreviousPublisher), Is.EqualTo(("BillingV2", "Billing")));

            billing.Instances[0].Stopped = true;
            Assert.That(Model(new[] { billing, billingV2 }).Validate(route).Findings.Single().Message, Does.Contain("complete the migration"));
        }

        static InstanceRecord Publishing(InstanceRecord instance, params string[] eventTypes)
        {
            instance.PublishedTypes.AddRange(eventTypes.Select(t => new PublishedType { Type = t }));
            return instance;
        }

        static RoutingModel Model(IReadOnlyList<EndpointRecord> endpoints, IReadOnlyList<RouteRecord> routes = null,
            IReadOnlyList<PeerRecord> peers = null, IReadOnlyList<SubscriptionRequestRecord> requests = null)
        {
            var sites = new[]
            {
                new RouterSite("a", "a", new[] { "id-a" }, "Router@a"),
                new RouterSite("b", "b", new[] { "id-b" }, "Router@b")
            };
            return new RoutingModel("Router", sites, endpoints, routes ?? new List<RouteRecord>(), new List<SiteIdentityRecord>(), now, expiry,
                peers, requests);
        }

        static EndpointRecord Endpoint(string name, params InstanceRecord[] instances)
        {
            foreach (var instance in instances)
            {
                instance.InputQueue ??= name;
            }

            return new EndpointRecord
            {
                Name = name,
                Instances = instances.ToList(),
                Sites = instances.Select(i => i.SiteId).Distinct()
                    .Select(s => new EndpointSiteRecord { SiteId = s, FirstSeen = now - TimeSpan.FromDays(1) }).ToList()
            };
        }

        static InstanceRecord Instance(string id, string site, string handledType, string otherHandledType = null, DateTimeOffset? lastReport = null)
        {
            var instance = new InstanceRecord { InstanceId = id, SiteId = site, LastReportAt = lastReport ?? now };
            instance.HandledTypes.Add(new HandledType { Type = handledType });
            if (otherHandledType != null)
            {
                instance.HandledTypes.Add(new HandledType { Type = otherHandledType });
            }
            return instance;
        }
    }
}
