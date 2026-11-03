using System;
using CentralRouting.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

static class PartRouter
{
    /// <summary>
    /// A part's router: attached to the part's site and to the backplane, peering with every other part's router over the backplane.
    /// </summary>
    public static void Run(string[] args, Part part)
    {
        Console.Title = part.RouterName;

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(part.RouterUrl.ToString());

        var router = new RouterConfiguration(part.RouterName)
        {
            HeartbeatInterval = TimeSpan.FromSeconds(10),
            PeerHeartbeatInterval = TimeSpan.FromSeconds(30)
        };

        var local = router.AddInterface("local", part.CreateTransport());
        local.Site = part.SiteId;
        local.SiteIdentities.Add(part.Identity);

        var backplane = router.AddInterface("backplane", AzureServiceBusNamespace.CreateTransport());
        backplane.Site = Parts.BackplaneSiteId;
        backplane.SiteIdentities.Add(AzureServiceBusNamespace.Identity);

        foreach (var other in Parts.Others(part))
        {
            router.AddPeer(other.RouterName, "backplane");
        }

        router.UseSqlServerStorage(part.ConnectionString);

        //Set as environment variables or arguments (--CentralRouting_Sample_Deduplication false), or pick a launch profile
        var duplicate = builder.Configuration.GetValue("CentralRouting_Sample_DuplicateOnBackplane", false);
        var deduplicate = builder.Configuration.GetValue("CentralRouting_Sample_Deduplication", true);

        if (duplicate)
        {
            router.AddForwardingBehavior<BackplaneDuplicationBehavior>();
        }

        //The part's endpoints process exactly once in the SQL Server transport's transaction, but the backplane delivers at least once
        if (deduplicate)
        {
            router.AddSqlServerDeduplication(part.SiteId, part.ConnectionString);
        }

        builder.Services.AddCentralRouter(router);

        var app = builder.Build();
        app.Logger.LogInformation("Duplicating messages sent over the backplane: {Duplicate}. Deduplicating messages forwarded into {Site}: {Deduplicate}",
            duplicate ? "on" : "off", part.SiteId, deduplicate ? "on" : "off");
        app.MapCentralRoutingApi();
        app.Run();
    }
}
