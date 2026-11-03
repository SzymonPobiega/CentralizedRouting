using System;
using CentralRouting.Model;
using Microsoft.Data.SqlClient;
using NServiceBus;

/// <summary>
/// The on-premises site: a SQL Server transport database on the local SQL Server instance.
/// </summary>
static class SqlServerSite
{
    public const string SiteId = "on-premises";

    public static readonly string ConnectionString = Environment.GetEnvironmentVariable("CentralRouting_Sample_SqlServerTransport")
                                                     ?? "Server=.;Database=CentralRoutingTopology1;Integrated Security=true;TrustServerCertificate=true;Max Pool Size=100";

    public static string Identity => SiteIdentity.ForSqlServer(ConnectionString);

    public static SqlServerTransport CreateTransport()
    {
        EnsureDatabase();
        return new SqlServerTransport(ConnectionString)
        {
            //No distributed transactions: the router forwards between SQL Server and Azure Service Bus
            TransportTransactionMode = TransportTransactionMode.SendsAtomicWithReceive
        };
    }

    /// <summary>
    /// The transport creates its tables but not the database.
    /// </summary>
    static void EnsureDatabase()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;
        builder.InitialCatalog = "master";

        using var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(@database) IS NULL EXEC('CREATE DATABASE [{database.Replace("]", "]]")}]')";
        command.Parameters.AddWithValue("@database", database);
        command.ExecuteNonQuery();
    }
}
