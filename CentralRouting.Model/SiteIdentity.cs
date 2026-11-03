using System;
using System.Data.Common;
using System.IO;
using System.Linq;

namespace CentralRouting.Model
{
    /// <summary>
    /// Builds normalized site identities. Instances that connect to the same broker produce the same identity.
    /// Credentials and tuning options are never part of an identity.
    /// </summary>
    public static class SiteIdentity
    {
        public static string ForLearningTransport(string storageDirectory)
        {
            var path = Path.GetFullPath(storageDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return $"learning:v1:{path.ToLowerInvariant()}";
        }

        /// <summary>
        /// Server, database and schema, e.g. <c>sqlserver:v1:dc2-sql01/branch/dbo</c>.
        /// </summary>
        public static string ForSqlServer(string connectionString, string schema = "dbo")
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var server = Find(builder, "Server", "Data Source", "Address", "Addr", "Network Address")
                         ?? throw new ArgumentException("The connection string has no server.", nameof(connectionString));
            var database = Find(builder, "Database", "Initial Catalog")
                           ?? throw new ArgumentException("The connection string has no database.", nameof(connectionString));

            //"tcp:host,1433" and "host" name the same server
            server = server.Trim();
            if (server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            {
                server = server.Substring(4);
            }
            if (server.EndsWith(",1433", StringComparison.Ordinal))
            {
                server = server.Substring(0, server.Length - 5);
            }

            return $"sqlserver:v1:{server}/{database}/{schema}".ToLowerInvariant();
        }

        /// <summary>
        /// The namespace's fully qualified name, e.g. <c>asb:v1:contoso-hq.servicebus.windows.net</c>.
        /// Takes a connection string or the namespace name itself.
        /// </summary>
        public static string ForAzureServiceBus(string connectionStringOrNamespace)
        {
            var value = connectionStringOrNamespace.Trim();
            if (value.Contains('='))
            {
                var builder = new DbConnectionStringBuilder { ConnectionString = value };
                value = Find(builder, "Endpoint")
                        ?? throw new ArgumentException("The connection string has no endpoint.", nameof(connectionStringOrNamespace));
            }

            var host = Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : value.TrimEnd('/');
            return $"asb:v1:{host}".ToLowerInvariant();
        }

        /// <summary>
        /// Sorted host list plus virtual host, e.g. <c>rabbitmq:v1:dc2-mq-1,dc2-mq-2/branch</c>.
        /// Takes the transport's connection string (<c>host=...;virtualHost=...</c>) or an AMQP URI.
        /// </summary>
        public static string ForRabbitMQ(string connectionString)
        {
            string hosts;
            string virtualHost;
            if (Uri.TryCreate(connectionString.Trim(), UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("amqp", StringComparison.OrdinalIgnoreCase))
            {
                hosts = uri.IsDefaultPort || uri.Port == 5672 || uri.Port == 5671 ? uri.Host : $"{uri.Host}:{uri.Port}";
                virtualHost = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            }
            else
            {
                var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
                hosts = Find(builder, "host") ?? throw new ArgumentException("The connection string has no host.", nameof(connectionString));
                virtualHost = Find(builder, "virtualHost", "virtual host");
            }

            var sortedHosts = hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => h.EndsWith(":5672", StringComparison.Ordinal) ? h.Substring(0, h.Length - 5) : h)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase);
            return $"rabbitmq:v1:{string.Join(",", sortedHosts)}/{(string.IsNullOrEmpty(virtualHost) ? "/" : virtualHost)}".ToLowerInvariant();
        }

        static string Find(DbConnectionStringBuilder builder, params string[] keys)
        {
            return keys.Select(k => builder.TryGetValue(k, out var value) ? value?.ToString() : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }
    }
}
