using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using CentralRouting.NServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NServiceBus;
using NServiceBus.Transport;

/// <summary>
/// Configures an endpoint the same way for every endpoint of the sample.
/// </summary>
static class SampleEndpoint
{
    public const string RouterName = "Router";
    public static readonly Uri RouterUrl = new Uri("http://localhost:5080");

    /// <summary>
    /// Pass <c>--instance 2</c> to run another instance of the endpoint from the same folder; otherwise both would share the host ID.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(string[] args, string endpointName, TransportDefinition transport, string siteIdentity,
        Action<CentralRoutingSettings> configureRouting = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var instance = builder.Configuration["instance"];
        Console.Title = instance == null ? endpointName : $"{endpointName} ({instance})";

        //Without a cached routing document an endpoint cannot start while the router is down, e.g. on the first F5 with several startup projects
        WaitForRouter(Path.Combine(Path.GetTempPath(), "CentralRoutingSamples", "Topology1", endpointName));

        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Configuration.AddCentralRouting(o =>
        {
            o.RouterUrl = RouterUrl;
            o.Cache = RoutingDocumentCache.FileSystem(Path.Combine(Path.GetTempPath(), "CentralRoutingSamples", "Topology1", endpointName));
            o.ReloadInterval = TimeSpan.FromSeconds(5);
        });

        var endpointConfiguration = new EndpointConfiguration(endpointName);
        endpointConfiguration.UseSerialization<SystemJsonSerializer>();
        endpointConfiguration.UseTransport(transport);
        endpointConfiguration.EnableInstallers();
        endpointConfiguration.Recoverability().Delayed(d => d.NumberOfRetries(0));

        var routing = endpointConfiguration.UseCentralRouting(RouterName, builder.Configuration);
        //Endpoints on SQL Server and Azure Service Bus tell the router which broker they use
        routing.SiteIdentity = siteIdentity;
        routing.HeartbeatInterval = TimeSpan.FromSeconds(10);
        if (instance != null)
        {
            routing.InstanceId = $"{Environment.MachineName}-{endpointName}-{instance}";
        }
        configureRouting?.Invoke(routing);

        builder.Services.AddNServiceBusEndpoint(endpointConfiguration);
        return builder;
    }

    static void WaitForRouter(string cacheDirectory)
    {
        if (Directory.Exists(cacheDirectory) && Directory.EnumerateFiles(cacheDirectory).Any())
        {
            return;
        }

        using var http = new HttpClient { BaseAddress = RouterUrl, Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                http.GetAsync("api/v1/sites").GetAwaiter().GetResult().EnsureSuccessStatusCode();
                return;
            }
            catch (Exception) when (attempt < 60)
            {
                if (attempt == 0)
                {
                    Console.WriteLine($"Waiting for the router at {RouterUrl}...");
                }
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }
    }
}
