using System;
using CentralRouting.Model;
using NServiceBus;

/// <summary>
/// Topology 2: one router, a site per service. Each service has its own RabbitMQ virtual host.
/// </summary>
static class Sites
{
    public const string Sample = "Topology2";
    public const string RouterName = "Router";
    public static readonly Uri RouterUrl = new Uri("http://localhost:5080");

    public static readonly ServiceSite Sales = new ServiceSite("sales");
    public static readonly ServiceSite Billing = new ServiceSite("billing");
    public static readonly ServiceSite Shipping = new ServiceSite("shipping");
    public static readonly ServiceSite[] All = { Sales, Billing, Shipping };
}

/// <summary>
/// A service's site: its own virtual host on the shared RabbitMQ broker. The site ID is the virtual host's name.
/// </summary>
class ServiceSite
{
    public ServiceSite(string virtualHost)
    {
        SiteId = virtualHost;
        VirtualHost = virtualHost;
    }

    public string SiteId { get; }
    public string VirtualHost { get; }
    public string ConnectionString => RabbitMQBroker.ConnectionString(VirtualHost);
    public string Identity => SiteIdentity.ForRabbitMQ(ConnectionString);

    public RabbitMQTransport CreateTransport()
    {
        RabbitMQBroker.EnsureVirtualHost(VirtualHost);
        return new RabbitMQTransport(RoutingTopology.Conventional(QueueType.Quorum), ConnectionString);
    }
}
