using System;
using CentralRouting.Router.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace CentralRouting.Router
{
    public static class EntityFrameworkStorageExtensions
    {
        /// <summary>
        /// Stores the router's state with Entity Framework Core. The tables are created on startup if they don't exist.
        /// </summary>
        /// <param name="configuration">The router configuration.</param>
        /// <param name="configureProvider">Selects and configures the database provider.</param>
        public static void UseEntityFrameworkStorage(this RouterConfiguration configuration, Action<DbContextOptionsBuilder> configureProvider)
        {
            var builder = new DbContextOptionsBuilder<RouterDbContext>();
            configureProvider(builder);
            var options = builder.Options;

            configuration.UseStorage(_ => new EntityFrameworkRouterStore(options));
        }
    }
}
