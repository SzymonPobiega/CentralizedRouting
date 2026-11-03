using System;
using CentralRouting.Model;
using NServiceBus;

/// <summary>
/// A branch: its own virtual host on the RabbitMQ broker, and the branch router that owns it. The site ID is the virtual host's name.
/// </summary>
class Branch
{
    public static readonly Branch A = new Branch("A", 5091);
    public static readonly Branch B = new Branch("B", 5092);

    Branch(string name, int routerPort)
    {
        Name = name;
        SiteId = Sites.BranchSiteId(name);
        RouterName = $"Branch{name}";
        RouterUrl = new Uri($"http://localhost:{routerPort}");
    }

    public string Name { get; }
    public string SiteId { get; }
    public string RouterName { get; }
    public Uri RouterUrl { get; }
    public string ConnectionString => RabbitMQBroker.ConnectionString(SiteId);
    public string Identity => SiteIdentity.ForRabbitMQ(ConnectionString);

    public RabbitMQTransport CreateTransport()
    {
        RabbitMQBroker.EnsureVirtualHost(SiteId);
        return new RabbitMQTransport(RoutingTopology.Conventional(QueueType.Quorum), ConnectionString);
    }
}
