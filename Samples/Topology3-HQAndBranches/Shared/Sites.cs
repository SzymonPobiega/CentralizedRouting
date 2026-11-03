using System;

/// <summary>
/// Topology 3: HQ and branches. HQ runs on Azure Service Bus and its router owns the HQ endpoints. Each branch runs a copy
/// of the branch system on its own RabbitMQ virtual host, with a router attached to the branch and to HQ.
/// </summary>
static class Sites
{
    public const string Sample = "Topology3";

    public const string HqSiteId = "hq";
    public const string HqRouterName = "HQ";
    public static readonly Uri HqRouterUrl = new Uri("http://localhost:5090");

    public static readonly string[] BranchNames = { "A", "B" };

    /// <summary>
    /// The site of a branch, e.g. branch-a. Senders at HQ name it with RouteToSite.
    /// </summary>
    public static string BranchSiteId(string branch) => $"branch-{branch.ToLowerInvariant()}";

    /// <summary>
    /// Each router keeps its store in its own database on the local SQL Server.
    /// </summary>
    public static string RouterStore(string routerName)
    {
        var server = Environment.GetEnvironmentVariable("CentralRouting_Sample_SqlServer") ?? ".";
        return $"Server={server};Database=CentralRoutingTopology3{routerName};Integrated Security=true;TrustServerCertificate=true";
    }
}
