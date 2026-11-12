using System;
using NServiceBus.Community.Routing;
using Microsoft.Extensions.Hosting;

static class ServiceEndpoint
{
    public static HostApplicationBuilder CreateBuilder(string[] args, string endpointName, ServiceSite site, Action<CentralRoutingSettings> configureRouting = null)
    {
        return SampleEndpoint.CreateBuilder(args, Sites.Sample, endpointName, site.CreateTransport(), site.Identity, Sites.RouterName, Sites.RouterUrl, configureRouting);
    }
}
