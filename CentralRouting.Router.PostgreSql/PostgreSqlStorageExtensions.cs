using Microsoft.EntityFrameworkCore;

namespace CentralRouting.Router
{
    public static class PostgreSqlStorageExtensions
    {
        /// <summary>
        /// Stores the router's state in PostgreSQL.
        /// </summary>
        public static void UsePostgreSqlStorage(this RouterConfiguration configuration, string connectionString)
        {
            configuration.UseEntityFrameworkStorage(options => options.UseNpgsql(connectionString));
        }
    }
}
