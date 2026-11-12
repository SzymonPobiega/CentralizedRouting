using System;
using NServiceBus.Community.Routing.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

//Topology 2: one router, a site per service. The router has an interface on each service's virtual host.
//Routes between services always go through the router; routes inside a service stay on that service's site.
var builder = WebApplication.CreateBuilder(args);

var router = new RouterConfiguration(Sites.RouterName)
{
    HeartbeatInterval = TimeSpan.FromSeconds(10)
};

foreach (var site in Sites.All)
{
    var routerInterface = router.AddInterface(site.SiteId, site.CreateTransport());
    routerInterface.Site = site.SiteId;
    routerInterface.SiteIdentities.Add(site.Identity);
}

router.UseSqlServerStorage(builder.Configuration.GetConnectionString("RouterStore"));

builder.Services.AddCentralRouter(router);

var app = builder.Build();
app.MapCentralRoutingApi();
app.Run();
