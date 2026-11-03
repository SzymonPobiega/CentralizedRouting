using System;
using CentralRouting.NServiceBus;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.Configuration
{
    public static class RoutingDocumentConfigurationExtensions
    {
        /// <summary>
        /// Adds the routing document published by a router to the configuration.
        /// </summary>
        public static IConfigurationBuilder AddCentralRouting(this IConfigurationBuilder builder, Action<RoutingDocumentSourceOptions> configure)
        {
            var options = new RoutingDocumentSourceOptions();
            configure(options);
            return builder.Add(new RoutingDocumentConfigurationSource(options));
        }
    }
}
