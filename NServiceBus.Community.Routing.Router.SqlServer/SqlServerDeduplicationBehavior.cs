using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NServiceBus.Community.Routing.Router
{
    public static class SqlServerDeduplicationExtensions
    {
        /// <summary>
        /// Forwards each message into a SQL Server transport site at most once. Endpoints on such a site can rely on exactly-once
        /// processing, because they change business data in the transport's transaction, but a message that crossed an
        /// at-least-once transport on its way there, such as a backplane, can arrive twice.
        /// The message ID is recorded in a table in the site's database, in the same transaction as the forwarded messages.
        /// </summary>
        public static void AddSqlServerDeduplication(this RouterConfiguration configuration, string site, string connectionString,
            string schema = "dbo", string table = "RouterDeduplication")
        {
            configuration.AddForwardingBehavior(sp => new SqlServerDeduplicationBehavior(site, connectionString, schema, table,
                sp.GetRequiredService<ILogger<SqlServerDeduplicationBehavior>>()));
        }
    }

    /// <summary>
    /// Records the ID of each message forwarded into a site, and drops a message whose ID is already recorded. The insert and the
    /// dispatch share one SQL transaction, so a message is either forwarded and recorded, or neither. When the incoming
    /// message is redelivered after that transaction committed, e.g. because acknowledging it failed, it is dropped.
    /// Rows are never removed; <c>InsertedAt</c> lets a cleanup job remove old ones.
    /// </summary>
    public class SqlServerDeduplicationBehavior : IForwardingBehavior
    {
        //Keys the SQL Server transport reads to dispatch in a transaction it didn't open
        const string connectionKey = "System.Data.SqlClient.SqlConnection";
        const string transactionKey = "System.Data.SqlClient.SqlTransaction";
        const string userProvidedKey = "SqlServer.Transaction.IsUserProvided";

        readonly string site;
        readonly string connectionString;
        readonly string tableName;
        readonly ILogger logger;
        readonly SemaphoreSlim tableLock = new SemaphoreSlim(1, 1);
        volatile bool tableCreated;

        public SqlServerDeduplicationBehavior(string site, string connectionString, string schema, string table, ILogger<SqlServerDeduplicationBehavior> logger)
        {
            this.site = site;
            this.connectionString = connectionString;
            tableName = $"[{schema}].[{table}]";
            this.logger = logger;
        }

        public async Task Invoke(ForwardingContext context, Func<Task> next)
        {
            if (context.Messages.All(m => m.Site != site))
            {
                await next().ConfigureAwait(false);
                return;
            }

            await EnsureTable(context.CancellationToken).ConfigureAwait(false);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(context.CancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(context.CancellationToken).ConfigureAwait(false);

            if (!await TryRecord(connection, transaction, context.MessageId, context.CancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation("Message {MessageId} was already forwarded to site {Site}. Dropping the duplicate", context.MessageId, site);
                context.Messages.RemoveAll(m => m.Site == site);
                await next().ConfigureAwait(false);
                return;
            }

            var dispatchTransaction = context.GetDispatchTransaction(site);
            dispatchTransaction.Set(connectionKey, connection);
            dispatchTransaction.Set(transactionKey, transaction);
            dispatchTransaction.Set(userProvidedKey, true);

            await next().ConfigureAwait(false);
            await transaction.CommitAsync(context.CancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Inserts the message ID. A concurrent copy of the same message waits on the key until the first commits, then finds it.
        /// </summary>
        async Task<bool> TryRecord(SqlConnection connection, SqlTransaction transaction, string messageId, CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand($"INSERT INTO {tableName} (MessageId, InsertedAt) VALUES (@MessageId, SYSUTCDATETIME())", connection, transaction);
            command.Parameters.Add("@MessageId", System.Data.SqlDbType.NVarChar, 200).Value = messageId;
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (SqlException e) when (e.Number is 2627 or 2601)
            {
                return false;
            }
        }

        async Task EnsureTable(CancellationToken cancellationToken)
        {
            if (tableCreated)
            {
                return;
            }

            await tableLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (tableCreated)
                {
                    return;
                }

                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = new SqlCommand(
                    $"IF OBJECT_ID(N'{tableName}', N'U') IS NULL CREATE TABLE {tableName} (MessageId NVARCHAR(200) NOT NULL PRIMARY KEY, InsertedAt DATETIME2 NOT NULL)",
                    connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                tableCreated = true;
            }
            finally
            {
                tableLock.Release();
            }
        }
    }
}
