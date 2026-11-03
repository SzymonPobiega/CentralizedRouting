using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CentralRouting.Router;
using NUnit.Framework;

namespace CentralRouting.AcceptanceTests
{
    [TestFixture]
    public class When_endpoints_send_heartbeats
    {
        [Test]
        public async Task The_revision_stays_the_same()
        {
            await using var environment = await RoutingTestEnvironment.Start(StoreKind.InMemory);

            await environment.StartEndpoint("Sales", RoutingTestEnvironment.SiteA);
            await environment.WaitForEndpoints(0, "Sales");
            var revision = await environment.CurrentRevision();
            await environment.WaitForEndpoints(revision, "Sales");
            revision = await environment.CurrentRevision();

            //Instances expire after three missed heartbeats, three seconds here
            await Task.Delay(TimeSpan.FromSeconds(5));

            Assert.That(await environment.CurrentRevision(), Is.EqualTo(revision));
            var sales = (await environment.Get<List<EndpointView>>("endpoints")).Single(e => e.Name == "Sales").Instances.Single();
            Assert.That(sales.Status, Is.EqualTo(InstanceStatus.Live));
            Assert.That(DateTimeOffset.UtcNow - sales.LastReportAt, Is.LessThan(TimeSpan.FromSeconds(2.5)));
        }
    }
}
