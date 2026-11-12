using System;
using NServiceBus.Community.Routing.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

//Topology 5: migration between sites. The router owns both sites, as in Topology 1. While an endpoint has instances on both,
//the NewestSite policy sends its traffic to the site where it appeared last, and the old instances drain their queue.
Console.Title = Sites.RouterName;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Sites.RouterUrl.ToString());

var router = new RouterConfiguration(Sites.RouterName)
{
    HeartbeatInterval = TimeSpan.FromSeconds(10)
};

var rabbit = router.AddInterface("rabbit", Sites.CreateRabbitTransport());
rabbit.Site = Sites.Rabbit;
rabbit.SiteIdentities.Add(Sites.RabbitIdentity);

var azure = router.AddInterface("azure", AzureServiceBusNamespace.CreateTransport());
azure.Site = Sites.Azure;
azure.SiteIdentities.Add(AzureServiceBusNamespace.Identity);

router.UseSqlServerStorage(Sites.RouterStore);

builder.Services.AddCentralRouter(router);

var app = builder.Build();
app.MapCentralRoutingApi();
app.Run();
