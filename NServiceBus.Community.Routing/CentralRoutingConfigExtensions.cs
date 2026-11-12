using System;
using NServiceBus.Community.Routing;
using Microsoft.Extensions.Configuration;
using NServiceBus.Configuration.AdvancedExtensibility;
using NServiceBus.Extensibility;
using NServiceBus.Features;

// ReSharper disable once CheckNamespace
namespace NServiceBus
{
    /// <summary>
    /// Extensions for configuring central routing.
    /// </summary>
    public static class CentralRoutingConfigExtensions
    {
        /// <summary>
        /// Routes messages with the routing document published by the given router, fetched from its HTTP API.
        /// </summary>
        /// <param name="config">The endpoint configuration.</param>
        /// <param name="routerName">Name of the router the endpoint reports to.</param>
        /// <param name="routerUrl">Base URL of the router's HTTP API.</param>
        public static CentralRoutingSettings UseCentralRouting(this EndpointConfiguration config, string routerName, Uri routerUrl)
        {
            var settings = Enable(config, routerName);
            settings.Document.RouterUrl = routerUrl;
            return settings;
        }

        /// <summary>
        /// Routes messages with the routing document in the configuration, added with
        /// <see cref="RoutingDocumentConfigurationExtensions.AddCentralRouting"/>.
        /// </summary>
        /// <param name="config">The endpoint configuration.</param>
        /// <param name="routerName">Name of the router the endpoint reports to.</param>
        /// <param name="configuration">Configuration that contains the routing document.</param>
        public static CentralRoutingSettings UseCentralRouting(this EndpointConfiguration config, string routerName, IConfiguration configuration)
        {
            var settings = Enable(config, routerName);
            settings.Configuration = configuration;
            return settings;
        }

        /// <summary>
        /// Sends the message to the given site of its destination endpoint.
        /// </summary>
        public static void RouteToSite(this SendOptions options, string site)
        {
            options.GetExtensions().Set(new RouteToSiteOption(site));
        }

        static CentralRoutingSettings Enable(EndpointConfiguration config, string routerName)
        {
            //Subscriptions are owned by the router
            config.DisableFeature<AutoSubscribe>();
            config.EnableFeature<CentralRoutingFeature>();

            var settings = new CentralRoutingSettings(routerName);
            config.GetSettings().Set(settings);
            return settings;
        }
    }
}
