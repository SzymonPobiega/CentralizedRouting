using System;
using CentralRouting.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

//Topology 3: HQ and branches. The HQ router has a single interface, on hq. It forwards nothing: it owns the HQ endpoints,
//and learns the branch endpoints from the branch routers. HQ senders go straight to a branch router's queue on hq.
Console.Title = Sites.HqRouterName;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Sites.HqRouterUrl.ToString());

var router = new RouterConfiguration(Sites.HqRouterName)
{
    HeartbeatInterval = TimeSpan.FromSeconds(10),
    PeerHeartbeatInterval = TimeSpan.FromSeconds(30)
};

var hq = router.AddInterface("hq", AzureServiceBusNamespace.CreateTransport());
hq.Site = Sites.HqSiteId;
hq.SiteIdentities.Add(AzureServiceBusNamespace.Identity);

foreach (var branch in Sites.BranchNames)
{
    router.AddPeer($"Branch{branch}", "hq");
}

router.UseSqlServerStorage(Sites.RouterStore(Sites.HqRouterName));

builder.Services.AddCentralRouter(router);

var app = builder.Build();
app.MapCentralRoutingApi();
app.Run();
