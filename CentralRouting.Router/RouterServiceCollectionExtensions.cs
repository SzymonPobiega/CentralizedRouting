using System.Linq;
using CentralRouting.Model;
using CentralRouting.Router;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Registers a router in an ASP.NET Core host.
    /// </summary>
    public static class RouterServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the router's services and the hosted service that runs its interfaces.
        /// Map its HTTP API with <see cref="RouterApi.MapCentralRoutingApi"/>.
        /// </summary>
        public static IServiceCollection AddCentralRouter(this IServiceCollection services, RouterConfiguration configuration)
        {
            services.AddSingleton(configuration);
            services.AddSingleton(sp => configuration.StoreFactory(sp));
            services.AddSingleton<RouterTopology>();
            services.AddSingleton(sp => new RoutingSnapshotProvider(sp.GetRequiredService<IRouterStore>(), configuration, sp.GetRequiredService<RouterTopology>()));
            services.AddSingleton<PeeringService>();
            services.AddSingleton(sp => new ForwardingPipeline(configuration.ForwardingBehaviors.Select(f => f(sp)), sp.GetRequiredService<RouterTopology>()));
            services.AddSingleton<ControlMessageHandler>();
            services.AddSingleton<Forwarder>();
            services.AddHostedService<RouterHostedService>();
            services.ConfigureHttpJsonOptions(options => RoutingJson.Configure(options.SerializerOptions));
            return services;
        }
    }
}
