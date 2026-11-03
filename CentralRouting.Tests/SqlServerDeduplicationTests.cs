using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralRouting.Router;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NServiceBus.Transport;
using NUnit.Framework;

namespace CentralRouting.Tests
{
    [TestFixture]
    public class SqlServerDeduplicationTests
    {
        static readonly string connectionString = Environment.GetEnvironmentVariable("CentralRouting_SqlServer_ConnectionString")
                                                  ?? "Server=.;Database=CentralRoutingStoreTests;Integrated Security=true;TrustServerCertificate=true";

        SqlServerDeduplicationBehavior behavior;

        [SetUp]
        public async Task SetUp()
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var database = builder.InitialCatalog;
            builder.InitialCatalog = "master";
            try
            {
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using var command = new SqlCommand($"IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}]", connection);
                await command.ExecuteNonQueryAsync();
            }
            catch (SqlException e)
            {
                Assert.Ignore($"SQL Server is not available: {e.Message}");
            }

            //A fresh table for each test
            var table = $"Dedup{Guid.NewGuid():N}";
            behavior = new SqlServerDeduplicationBehavior("sql", connectionString, "dbo", table, NullLogger<SqlServerDeduplicationBehavior>.Instance);
        }

        [Test]
        public async Task A_message_is_forwarded_into_the_site_once()
        {
            var first = Context("message-1", "sql", "other");
            TransportTransaction dispatchTransaction = null;
            await behavior.Invoke(first, () =>
            {
                dispatchTransaction = first.GetDispatchTransaction("sql");
                return Task.CompletedTask;
            });

            Assert.That(dispatchTransaction.TryGet("System.Data.SqlClient.SqlTransaction", out SqlTransaction _), Is.True, "The dispatch joins the transaction");
            Assert.That(first.Messages.Select(m => m.Site), Is.EqualTo(new[] { "sql", "other" }));

            var duplicate = Context("message-1", "sql", "other");
            await behavior.Invoke(duplicate, () => Task.CompletedTask);

            Assert.That(duplicate.Messages.Select(m => m.Site), Is.EqualTo(new[] { "other" }), "Only the copy for the deduplicated site is dropped");
        }

        [Test]
        public async Task A_failed_dispatch_does_not_record_the_message()
        {
            var failed = Context("message-2", "sql");
            Assert.ThrowsAsync<InvalidOperationException>(() => behavior.Invoke(failed, () => throw new InvalidOperationException("Dispatch failed")));

            var retry = Context("message-2", "sql");
            await behavior.Invoke(retry, () => Task.CompletedTask);

            Assert.That(retry.Messages, Has.Count.EqualTo(1), "The retry is forwarded");
        }

        [Test]
        public async Task Messages_for_other_sites_need_no_database()
        {
            var unreachable = new SqlServerDeduplicationBehavior("sql", "Server=unreachable;Connect Timeout=1", "dbo", "Dedup", NullLogger<SqlServerDeduplicationBehavior>.Instance);
            var context = Context("message-3", "other");
            var called = false;

            await unreachable.Invoke(context, () =>
            {
                called = true;
                return Task.CompletedTask;
            });

            Assert.That(called, Is.True);
        }

        static ForwardingContext Context(string messageId, params string[] sites)
        {
            var context = new ForwardingContext(messageId, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, "backplane", "backplane",
                new TransportTransaction(), CancellationToken.None);
            context.Messages.AddRange(sites.Select(s => new ForwardedMessage(s, "Billing", new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty)));
            return context;
        }
    }
}
