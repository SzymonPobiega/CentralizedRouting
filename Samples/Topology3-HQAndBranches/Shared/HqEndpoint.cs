using System;
using NServiceBus.Community.Routing;
using Microsoft.Extensions.Hosting;

static class HqEndpoint
{
    /// <summary>
    /// An endpoint on the hq site, reporting to the HQ router.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(string[] args, string endpointName, Action<CentralRoutingSettings> configureRouting = null)
    {
        return SampleEndpoint.CreateBuilder(args, Sites.Sample, endpointName, AzureServiceBusNamespace.CreateTransport(), AzureServiceBusNamespace.Identity,
            Sites.HqRouterName, Sites.HqRouterUrl, configureRouting);
    }
}
