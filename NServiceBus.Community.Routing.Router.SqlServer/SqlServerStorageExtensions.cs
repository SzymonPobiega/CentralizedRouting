using Microsoft.EntityFrameworkCore;

namespace NServiceBus.Community.Routing.Router
{
    public static class SqlServerStorageExtensions
    {
        /// <summary>
        /// Stores the router's state in SQL Server.
        /// </summary>
        public static void UseSqlServerStorage(this RouterConfiguration configuration, string connectionString)
        {
            configuration.UseEntityFrameworkStorage(options => options.UseSqlServer(connectionString));
        }
    }
}
