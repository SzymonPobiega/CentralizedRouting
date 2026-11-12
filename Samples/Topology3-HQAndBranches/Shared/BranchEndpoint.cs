using System;
using System.IO;
using NServiceBus.Community.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

static class BranchEndpoint
{
    /// <summary>
    /// An endpoint on the branch's site, reporting to the branch router. Every branch runs the same endpoints under the same names.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(string[] args, string endpointName, Branch branch)
    {
        var builder = SampleEndpoint.CreateBuilder(args, Path.Combine(Sites.Sample, branch.SiteId), endpointName, branch.CreateTransport(), branch.Identity,
            branch.RouterName, branch.RouterUrl);
        Console.Title = $"{endpointName} ({branch.SiteId})";
        builder.Services.AddSingleton(branch);
        return builder;
    }
}
