using System.Collections.Generic;
using System.Linq;
using CentralRouting.Model;
using NUnit.Framework;

namespace CentralRouting.Tests
{
    [TestFixture]
    public class AddressResolverTests
    {
        [Test]
        public void Distribute_weights_sites_by_instance_count()
        {
            var resolver = Resolver(AddressPolicy.Distribute, new() { ["a"] = Queues(("Billing", 1)), ["b"] = Queues(("Billing", 3)) });

            var picks = Enumerable.Range(0, 400).Select(_ => resolver.ChooseSite("Billing", "a", null)).ToList();

            Assert.That(picks.Count(s => s == "a"), Is.EqualTo(100));
            Assert.That(picks.Count(s => s == "b"), Is.EqualTo(300));
        }

        [Test]
        public void Queues_on_a_site_are_weighted_by_instance_count()
        {
            var resolver = Resolver(AddressPolicy.Strict, new() { ["a"] = Queues(("Billing-1", 2), ("Billing-2", 1)) });

            var picks = Enumerable.Range(0, 300).Select(_ => resolver.ChooseQueue("Billing", "a")).ToList();

            Assert.That(picks.Count(q => q == "Billing-1"), Is.EqualTo(200));
        }

        [TestCase(AddressPolicy.Strict, "a", ExpectedResult = "a")]
        [TestCase(AddressPolicy.Strict, "c", ExpectedResult = "a")]
        [TestCase(AddressPolicy.PreferLocal, "b", ExpectedResult = "b")]
        public string Picks_site_by_policy(AddressPolicy policy, string localSite)
        {
            var resolver = Resolver(policy, new() { ["a"] = Queues(("Billing", 1)), ["b"] = Queues(("Billing", 1)) });
            return resolver.ChooseSite("Billing", localSite, null);
        }

        [Test]
        public void PreferLocal_spreads_over_remote_sites_when_there_is_no_local_one()
        {
            var resolver = Resolver(AddressPolicy.PreferLocal, new() { ["a"] = Queues(("Billing", 1)), ["b"] = Queues(("Billing", 1)) });

            var picks = Enumerable.Range(0, 4).Select(_ => resolver.ChooseSite("Billing", "c", null)).ToList();

            Assert.That(picks, Is.EquivalentTo(new[] { "a", "b", "a", "b" }));
        }

        [Test]
        public void Explicit_requires_a_site()
        {
            var resolver = Resolver(AddressPolicy.Explicit, new() { ["a"] = Queues(("Billing", 1)), ["b"] = Queues(("Billing", 1)) });

            Assert.Throws<RoutingException>(() => resolver.ChooseSite("Billing", "a", null));
            Assert.That(resolver.ChooseSite("Billing", "a", "b"), Is.EqualTo("b"));
        }

        [Test]
        public void Requested_site_without_instances_fails()
        {
            var resolver = Resolver(AddressPolicy.Strict, new() { ["a"] = Queues(("Billing", 1)) });

            Assert.Throws<RoutingException>(() => resolver.ChooseSite("Billing", "a", "b"));
        }

        [Test]
        public void Event_targets_prefer_the_source_site_and_exclude_explicit_subscribers()
        {
            var document = new RoutingDocument
            {
                Endpoints =
                {
                    ["Local"] = new EndpointInfo { Policy = AddressPolicy.Strict, Addresses = { ["a"] = Queues(("Local", 1)), ["b"] = Queues(("Local", 1)) } },
                    ["Remote"] = new EndpointInfo { Policy = AddressPolicy.Strict, Addresses = { ["b"] = Queues(("Remote", 1)) } },
                    ["Branches"] = new EndpointInfo { Policy = AddressPolicy.Explicit, Addresses = { ["a"] = Queues(("Branches", 1)), ["b"] = Queues(("Branches", 1)) } }
                },
                Events = { ["Base"] = new EventRoute { Subscribers = { "Local", "Branches" } }, ["Derived"] = new EventRoute { Subscribers = { "Remote" } } }
            };
            var resolver = new AddressResolver(document);

            var targets = resolver.ResolveEvent(new[] { "Derived", "Base" }, "a");

            Assert.That(targets, Is.EquivalentTo(new[]
            {
                new Destination("Local", "a"),
                new Destination("Remote", "b")
            }), "Each site of an Explicit endpoint is a separate destination, which an event can't address");
        }

        static AddressResolver Resolver(AddressPolicy policy, Dictionary<string, Dictionary<string, int>> addresses)
        {
            var document = new RoutingDocument
            {
                Endpoints = { ["Billing"] = new EndpointInfo { Policy = policy, Addresses = addresses } }
            };
            return new AddressResolver(document);
        }

        static Dictionary<string, int> Queues(params (string Queue, int Instances)[] queues)
        {
            return queues.ToDictionary(q => q.Queue, q => q.Instances);
        }
    }
}
