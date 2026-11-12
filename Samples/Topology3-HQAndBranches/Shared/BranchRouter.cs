using System;
using NServiceBus.Community.Routing.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

static class BranchRouter
{
    /// <summary>
    /// A branch router: attached to the branch's site and to hq, peering with the HQ router. Branches don't peer with each other.
    /// </summary>
    public static void Run(string[] args, Branch branch)
    {
        Console.Title = branch.RouterName;

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(branch.RouterUrl.ToString());

        var router = new RouterConfiguration(branch.RouterName)
        {
            HeartbeatInterval = TimeSpan.FromSeconds(10),
            PeerHeartbeatInterval = TimeSpan.FromSeconds(30)
        };

        var local = router.AddInterface("local", branch.CreateTransport());
        local.Site = branch.SiteId;
        local.SiteIdentities.Add(branch.Identity);

        var hq = router.AddInterface("hq", AzureServiceBusNamespace.CreateTransport());
        hq.Site = Sites.HqSiteId;
        hq.SiteIdentities.Add(AzureServiceBusNamespace.Identity);

        router.AddPeer(Sites.HqRouterName, "hq");
        router.UseSqlServerStorage(Sites.RouterStore(branch.RouterName));

        builder.Services.AddCentralRouter(router);

        var app = builder.Build();
        app.MapCentralRoutingApi();
        app.Run();
    }
}
