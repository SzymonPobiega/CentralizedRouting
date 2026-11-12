using System;
using System.Linq;
using NServiceBus.Community.Routing.Model;
using NServiceBus;

/// <summary>
/// The backplane topology: each part runs on its own SQL Server database with its own router, and the routers peer over
/// an Azure Service Bus namespace that carries no endpoints.
/// </summary>
static class Parts
{
    public const string Sample = "Topology4";
    public const string BackplaneSiteId = "backplane";

    public static readonly Part Sales = new Part("sales", "SalesRouter", 5081);
    public static readonly Part Billing = new Part("billing", "BillingRouter", 5082);
    public static readonly Part Shipping = new Part("shipping", "ShippingRouter", 5083);
    public static readonly Part[] All = { Sales, Billing, Shipping };

    public static Part[] Others(Part part) => All.Where(p => p != part).ToArray();
}

/// <summary>
/// One part of the system: a site on its own SQL Server database, and the router that owns it.
/// The router's store lives in the same database.
/// </summary>
class Part
{
    static readonly string server = Environment.GetEnvironmentVariable("CentralRouting_Sample_SqlServer") ?? ".";

    public Part(string siteId, string routerName, int routerPort)
    {
        SiteId = siteId;
        RouterName = routerName;
        RouterUrl = new Uri($"http://localhost:{routerPort}");
        ConnectionString = $"Server={server};Database=CentralRoutingTopology4{char.ToUpperInvariant(siteId[0])}{siteId[1..]};Integrated Security=true;TrustServerCertificate=true;Max Pool Size=100";
    }

    public string SiteId { get; }
    public string RouterName { get; }
    public Uri RouterUrl { get; }
    public string ConnectionString { get; }
    public string Identity => SiteIdentity.ForSqlServer(ConnectionString);

    public SqlServerTransport CreateTransport()
    {
        SqlServerDatabase.EnsureExists(ConnectionString);
        return new SqlServerTransport(ConnectionString)
        {
            //No distributed transactions: the routers forward between SQL Server and Azure Service Bus
            TransportTransactionMode = TransportTransactionMode.SendsAtomicWithReceive
        };
    }
}
