using System;
using System.Collections.Generic;
using System.Linq;
using CentralRouting.Router;
using CentralRouting.Router.Host;
using Microsoft.Extensions.Configuration;
using NServiceBus;
using NUnit.Framework;

namespace CentralRouting.Tests
{
    [TestFixture]
    public class RouterSettingsTests
    {
        [Test]
        public void Builds_the_router_from_configuration()
        {
            var router = Build(new()
            {
                ["Router:Name"] = "Branch042",
                ["Router:HeartbeatInterval"] = "00:00:30",
                ["Router:Interfaces:local:Type"] = "SqlServer",
                ["Router:Interfaces:local:ConnectionString"] = "Server=dc2-sql01;Database=branch;User Id=router;Password=secret",
                ["Router:Interfaces:local:Site"] = "branch-042",
                ["Router:Interfaces:local:Deduplication"] = "true",
                ["Router:Interfaces:mq:Type"] = "RabbitMQ",
                ["Router:Interfaces:mq:ConnectionString"] = "host=dc2-mq;virtualHost=branch",
                ["Router:Interfaces:hq:Type"] = "AzureServiceBus",
                ["Router:Interfaces:hq:ConnectionString"] = "Endpoint=sb://contoso-hq.servicebus.windows.net/;SharedAccessKeyName=router;SharedAccessKey=secret",
                ["Router:Interfaces:hq:Site"] = "hq",
                ["Router:Interfaces:hq:SiteIdentities:0"] = "asb:v1:contoso-hq-dr.servicebus.windows.net",
                ["Router:Peers:HQ:Interface"] = "hq",
                ["Router:Peers:Central:Url"] = "https://central-router",
                ["Router:Peers:Branch043:Inbound"] = "true"
            });

            Assert.That(router.Name, Is.EqualTo("Branch042"));
            Assert.That(router.HeartbeatInterval, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(router.MaxHops, Is.EqualTo(8), "Settings left out keep the router's defaults");

            var local = router.Interfaces.Single(i => i.Name == "local");
            Assert.That(local.Site, Is.EqualTo("branch-042"));
            Assert.That(local.SiteIdentities, Is.EqualTo(new[] { "sqlserver:v1:dc2-sql01/branch/dbo" }));
            Assert.That(local.Transport.TransportTransactionMode, Is.EqualTo(TransportTransactionMode.SendsAtomicWithReceive));

            var mq = router.Interfaces.Single(i => i.Name == "mq");
            Assert.That(mq.Site, Is.EqualTo("Branch042.mq"));
            Assert.That(mq.SiteIdentities, Is.EqualTo(new[] { "rabbitmq:v1:dc2-mq/branch" }));

            var hq = router.Interfaces.Single(i => i.Name == "hq");
            Assert.That(hq.SiteIdentities, Is.EqualTo(new[] { "asb:v1:contoso-hq.servicebus.windows.net", "asb:v1:contoso-hq-dr.servicebus.windows.net" }));

            Assert.That(router.Peers.Select(p => (p.Name, p.Channel)), Is.EquivalentTo(new[]
            {
                ("HQ", PeerChannel.Interface), ("Central", PeerChannel.Http), ("Branch043", PeerChannel.Inbound)
            }));
            Assert.That(router.ForwardingBehaviors, Has.Count.EqualTo(1), "Deduplication into the SQL Server site");
        }

        [Test]
        public void Lists_every_problem_in_the_configuration()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => Build(new()
            {
                ["Router:Storage:Type"] = "PostgreSql",
                ["Router:Interfaces:mq:ConnectionString"] = "host=dc2-mq",
                ["Router:Interfaces:pg:Type"] = "PostgreSql",
                ["Router:Interfaces:pg:ConnectionString"] = "Host=pg;Database=branch",
                ["Router:Interfaces:pg:Deduplication"] = "true",
                ["Router:Peers:HQ:Interface"] = "hq",
                ["Router:Peers:HQ:Inbound"] = "true"
            }));

            Assert.That(exception.Message, Does.Contain("Router:Name is required"));
            Assert.That(exception.Message, Does.Contain("Router:Storage:ConnectionString is required for PostgreSql storage"));
            Assert.That(exception.Message, Does.Contain("Router:Interfaces:mq:Type is required"));
            Assert.That(exception.Message, Does.Contain("Router:Interfaces:pg:Deduplication is only supported on SQL Server"));
            Assert.That(exception.Message, Does.Contain("Router:Peers:HQ needs exactly one of Interface, Url and Inbound"));
        }

        static RouterConfiguration Build(Dictionary<string, string> values)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var settings = configuration.GetSection("Router").Get<RouterSettings>() ?? new RouterSettings();
            return settings.ToRouterConfiguration();
        }
    }
}
