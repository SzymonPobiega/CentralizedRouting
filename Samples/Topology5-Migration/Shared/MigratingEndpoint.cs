using System;
using System.IO;
using CentralRouting.NServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

static class MigratingEndpoint
{
    /// <summary>
    /// An endpoint that runs on either site: <c>--site rabbit</c> (the default) or <c>--site azure</c>. During its migration
    /// both run at once, as two instances of one endpoint. Each gets an instance ID with its site in it, because both
    /// would otherwise share the host ID of the folder they run from.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(string[] args, string endpointName, Action<CentralRoutingSettings> configureRouting = null)
    {
        var site = new ConfigurationBuilder().AddCommandLine(args).Build()["site"] ?? Sites.Rabbit;
        var builder = site switch
        {
            Sites.Rabbit => SampleEndpoint.CreateBuilder(args, Path.Combine(Sites.Sample, site), endpointName, Sites.CreateRabbitTransport(), Sites.RabbitIdentity,
                Sites.RouterName, Sites.RouterUrl, Configure),
            Sites.Azure => SampleEndpoint.CreateBuilder(args, Path.Combine(Sites.Sample, site), endpointName, AzureServiceBusNamespace.CreateTransport(), AzureServiceBusNamespace.Identity,
                Sites.RouterName, Sites.RouterUrl, Configure),
            _ => throw new Exception($"Unknown site '{site}'. Use --site {Sites.Rabbit} or --site {Sites.Azure}.")
        };
        Console.Title = $"{endpointName} ({site})";
        return builder;

        void Configure(CentralRoutingSettings routing)
        {
            routing.InstanceId = $"{Environment.MachineName}-{endpointName}-{site}";
            configureRouting?.Invoke(routing);
        }
    }
}
