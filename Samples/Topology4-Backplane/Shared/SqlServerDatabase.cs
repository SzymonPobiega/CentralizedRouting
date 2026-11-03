using Microsoft.Data.SqlClient;

static class SqlServerDatabase
{
    /// <summary>
    /// The SQL Server transport creates its tables but not the database.
    /// </summary>
    public static void EnsureExists(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
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
