using System;
using NServiceBus.Community.Routing.Model;
using NServiceBus;

/// <summary>
/// Topology 5: migration between sites. The system starts on RabbitMQ and moves to Azure Service Bus one endpoint at a time.
/// One router connects the two sites for as long as the migration lasts.
/// </summary>
static class Sites
{
    public const string Sample = "Topology5";
    public const string RouterName = "Router";
    public static readonly Uri RouterUrl = new Uri("http://localhost:5095");
    public static readonly string RouterStore =
        $"Server={Environment.GetEnvironmentVariable("CentralRouting_Sample_SqlServer") ?? "."};Database=CentralRoutingTopology5Router;Integrated Security=true;TrustServerCertificate=true";

    public const string Rabbit = "rabbit";
    public const string Azure = "azure";

    /// <summary>
    /// The RabbitMQ site is a virtual host of its own, so it doesn't mix with the other samples' queues.
    /// </summary>
    const string virtualHost = "topology5";

    public static string RabbitIdentity => SiteIdentity.ForRabbitMQ(RabbitMQBroker.ConnectionString(virtualHost));

    public static RabbitMQTransport CreateRabbitTransport()
    {
        RabbitMQBroker.EnsureVirtualHost(virtualHost);
        return new RabbitMQTransport(RoutingTopology.Conventional(QueueType.Quorum), RabbitMQBroker.ConnectionString(virtualHost));
    }
}
