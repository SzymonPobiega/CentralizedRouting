using System;
using NServiceBus.Community.Routing;
using Microsoft.Extensions.Hosting;

static class PartEndpoint
{
    /// <summary>
    /// An endpoint on the part's site, reporting to the part's router.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(string[] args, string endpointName, Part part, Action<CentralRoutingSettings> configureRouting = null)
    {
        return SampleEndpoint.CreateBuilder(args, Parts.Sample, endpointName, part.CreateTransport(), part.Identity, part.RouterName, part.RouterUrl, configureRouting);
    }
}
