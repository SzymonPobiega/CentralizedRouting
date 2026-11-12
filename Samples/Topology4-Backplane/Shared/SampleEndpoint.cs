using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using NServiceBus.Community.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NServiceBus;
using NServiceBus.Transport;

/// <summary>
/// Configures an endpoint the same way for every endpoint of every sample.
/// </summary>
static class SampleEndpoint
{
    /// <summary>
    /// Pass <c>--instance 2</c> to run another instance of the endpoint from the same folder; otherwise both would share the host ID.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(string[] args, string sample, string endpointName, TransportDefinition transport, string siteIdentity,
        string routerName, Uri routerUrl, Action<CentralRoutingSettings> configureRouting = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var instance = builder.Configuration["instance"];
        Console.Title = instance == null ? endpointName : $"{endpointName} ({instance})";

        //Without a cached routing document an endpoint cannot start while its router is down, e.g. on the first F5 with several startup projects
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "CentralRoutingSamples", sample, endpointName);
        WaitForRouter(routerUrl, cacheDirectory);

        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Configuration.AddCentralRouting(o =>
        {
            o.RouterUrl = routerUrl;
            o.Cache = RoutingDocumentCache.FileSystem(cacheDirectory);
            o.ReloadInterval = TimeSpan.FromSeconds(5);
        });

        var endpointConfiguration = new EndpointConfiguration(endpointName);
        endpointConfiguration.UseSerialization<SystemJsonSerializer>();
        endpointConfiguration.UseTransport(transport);
        endpointConfiguration.EnableInstallers();
        endpointConfiguration.Recoverability().Delayed(d => d.NumberOfRetries(0));

        var routing = endpointConfiguration.UseCentralRouting(routerName, builder.Configuration);
        //Endpoints on SQL Server, RabbitMQ and Azure Service Bus tell the router which broker they use
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

    static void WaitForRouter(Uri routerUrl, string cacheDirectory)
    {
        if (Directory.Exists(cacheDirectory) && Directory.EnumerateFiles(cacheDirectory).Any())
        {
            return;
        }

        using var http = new HttpClient { BaseAddress = routerUrl, Timeout = TimeSpan.FromSeconds(5) };
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
                    Console.WriteLine($"Waiting for the router at {routerUrl}...");
                }
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }
    }
}
