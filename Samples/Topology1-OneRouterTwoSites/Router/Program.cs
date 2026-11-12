using System;
using NServiceBus.Community.Routing.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

//Topology 1: one router, two sites. The router owns both sites: every endpoint reports to it,
//all routes are assigned on it, and it forwards everything that crosses between them.
var builder = WebApplication.CreateBuilder(args);

var router = new RouterConfiguration("Router")
{
    HeartbeatInterval = TimeSpan.FromSeconds(10)
};

var onPremises = router.AddInterface("sql", SqlServerSite.CreateTransport());
onPremises.Site = SqlServerSite.SiteId;
onPremises.SiteIdentities.Add(SqlServerSite.Identity);

var azure = router.AddInterface("asb", AzureServiceBusSite.CreateTransport());
azure.Site = AzureServiceBusSite.SiteId;
azure.SiteIdentities.Add(AzureServiceBusSite.Identity);

router.UseSqlServerStorage(builder.Configuration.GetConnectionString("RouterStore"));

builder.Services.AddCentralRouter(router);

var app = builder.Build();
app.MapCentralRoutingApi();
app.Run();
