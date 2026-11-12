using NServiceBus.Community.Routing.Model;
using NUnit.Framework;

namespace NServiceBus.Community.Routing.Tests
{
    [TestFixture]
    public class SiteIdentityTests
    {
        [Test]
        public void Sql_server_identity_ignores_credentials_options_and_the_default_port()
        {
            var endpoint = SiteIdentity.ForSqlServer("Server=tcp:SQL01,1433;Database=Sales;User Id=app;Password=secret;Max Pool Size=50");
            var router = SiteIdentity.ForSqlServer("Data Source=sql01;Initial Catalog=sales;Integrated Security=true");

            Assert.That(endpoint, Is.EqualTo("sqlserver:v1:sql01/sales/dbo"));
            Assert.That(router, Is.EqualTo(endpoint));
        }

        [Test]
        public void PostgreSql_identity_ignores_credentials_options_and_the_default_port()
        {
            var endpoint = SiteIdentity.ForPostgreSql("Host=Client-Pi;Port=5432;Database=Booth;Username=app;Password=secret;Pooling=true");
            var router = SiteIdentity.ForPostgreSql("Server=client-pi;Database=booth;Username=router;Password=other");

            Assert.That(endpoint, Is.EqualTo("postgresql:v1:client-pi/booth/public"));
            Assert.That(router, Is.EqualTo(endpoint));
            Assert.That(SiteIdentity.ForPostgreSql("Host=client-pi;Port=54320;Database=booth", "orders"), Is.EqualTo("postgresql:v1:client-pi:54320/booth/orders"));
        }

        [Test]
        public void RabbitMQ_identity_is_the_sorted_hosts_and_virtual_host()
        {
            var connectionString = SiteIdentity.ForRabbitMQ("host=dc2-mq-2,DC2-MQ-1:5672;virtualHost=Branch;username=app;password=secret");
            var uri = SiteIdentity.ForRabbitMQ("amqp://app:secret@dc2-mq-1/Branch");

            Assert.That(connectionString, Is.EqualTo("rabbitmq:v1:dc2-mq-1,dc2-mq-2/branch"));
            Assert.That(uri, Is.EqualTo("rabbitmq:v1:dc2-mq-1/branch"));
            Assert.That(SiteIdentity.ForRabbitMQ("host=localhost"), Is.EqualTo("rabbitmq:v1:localhost//"), "Default virtual host");
        }

        [Test]
        public void Azure_service_bus_identity_is_the_namespace()
        {
            var fromConnectionString = SiteIdentity.ForAzureServiceBus(
                "Endpoint=sb://Contoso-HQ.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=secret");

            Assert.That(fromConnectionString, Is.EqualTo("asb:v1:contoso-hq.servicebus.windows.net"));
            Assert.That(SiteIdentity.ForAzureServiceBus("contoso-hq.servicebus.windows.net"), Is.EqualTo(fromConnectionString));
        }
    }
}
