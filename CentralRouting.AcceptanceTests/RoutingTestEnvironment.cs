using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CentralRouting.Model;
using CentralRouting.NServiceBus;
using CentralRouting.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NServiceBus;

namespace CentralRouting.AcceptanceTests
{
    public enum StoreKind
    {
        InMemory,
        SqlServer
    }

    /// <summary>
    /// Routers and endpoints connected by sites, each site a separate learning transport directory.
    /// </summary>
    class RoutingTestEnvironment : IAsyncDisposable
    {
        public const string RouterName = "Router";
        public const string SiteA = "site-a";
        public const string SiteB = "site-b";

        static readonly string sqlServerConnectionString = Environment.GetEnvironmentVariable("CentralRouting_SqlServer_ConnectionString")
                                                           ?? "Server=.;Database=CentralRoutingTests;Integrated Security=true;TrustServerCertificate=true";

        readonly string rootDirectory = Path.Combine(Path.GetTempPath(), "CentralRoutingTests", Guid.NewGuid().ToString("N"));
        readonly List<IHost> endpoints = new List<IHost>();
        readonly List<TestRouter> routers = new List<TestRouter>();

        /// <summary>
        /// The router started by <see cref="Start"/>.
        /// </summary>
        public TestRouter Default => routers[0];

        public string SiteDirectory(string site) => Path.Combine(rootDirectory, site);

        public LearningTransport Site(string site) => new LearningTransport { StorageDirectory = SiteDirectory(site) };

        /// <summary>
        /// One router attached to two sites.
        /// </summary>
        public static async Task<RoutingTestEnvironment> Start(StoreKind storeKind, Action<RouterConfiguration> customize = null)
        {
            var environment = new RoutingTestEnvironment();
            await environment.StartRouter(RouterName, configuration =>
            {
                configuration.AddInterface("a", environment.Site(SiteA)).Site = SiteA;
                configuration.AddInterface("b", environment.Site(SiteB)).Site = SiteB;
                customize?.Invoke(configuration);
            }, storeKind).ConfigureAwait(false);
            return environment;
        }

        public async Task<TestRouter> StartRouter(string name, Action<RouterConfiguration> configure, StoreKind storeKind = StoreKind.InMemory)
        {
            var configuration = new RouterConfiguration(name)
            {
                HeartbeatInterval = TimeSpan.FromSeconds(1),
                RefreshInterval = TimeSpan.FromMilliseconds(200),
                PeerHeartbeatInterval = TimeSpan.FromSeconds(1)
            };
            configure(configuration);

            if (storeKind == StoreKind.SqlServer)
            {
                await ResetSqlServerDatabase().ConfigureAwait(false);
                configuration.UseSqlServerStorage(sqlServerConnectionString);
            }

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddCentralRouter(configuration);

            var application = builder.Build();
            application.MapCentralRoutingApi();
            await application.StartAsync().ConfigureAwait(false);

            var router = new TestRouter(name, application);
            routers.Add(router);
            return router;
        }

        public Task<IMessageSession> StartEndpoint(string name, string site, params Type[] handlers) => StartEndpoint(name, site, Default, null, handlers);

        public Task<IMessageSession> StartEndpoint(string name, string site, Action<CentralRoutingSettings> configureRouting, params Type[] handlers) =>
            StartEndpoint(name, site, Default, configureRouting, handlers);

        public Task<IMessageSession> StartEndpoint(string name, string site, TestRouter router, params Type[] handlers) =>
            StartEndpoint(name, site, router, null, handlers);

        public async Task<IMessageSession> StartEndpoint(string name, string site, TestRouter router, Action<CentralRoutingSettings> configureRouting,
            params Type[] handlers)
        {
            var configuration = new EndpointConfiguration(name);
            configuration.UseSerialization<SystemJsonSerializer>();
            configuration.UseTransport(Site(site));
            configuration.EnableInstallers();
            configuration.Recoverability().Delayed(d => d.NumberOfRetries(0));

            //Each endpoint gets only its own handlers out of the test assembly
            var otherHandlers = typeof(RoutingTestEnvironment).Assembly.GetTypes()
                .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IHandleMessages<>)))
                .Except(handlers)
                .ToArray();
            configuration.AssemblyScanner().ExcludeTypes(otherHandlers);

            var routing = configuration.UseCentralRouting(router.Name, router.Url);
            routing.InstanceId = $"{name}-{site}";
            routing.HeartbeatInterval = TimeSpan.FromSeconds(1);
            routing.Document.ReloadInterval = TimeSpan.FromMilliseconds(300);
            configureRouting?.Invoke(routing);

            var builder = Host.CreateApplicationBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddNServiceBusEndpoint(configuration);

            var host = builder.Build();
            await host.StartAsync().ConfigureAwait(false);
            endpoints.Add(host);
            return host.Services.GetRequiredService<IMessageSession>();
        }

        public Task<long> Put(string path, object body) => Default.Put(path, body);
        public Task<long> Post(string path, object body) => Default.Post(path, body);
        public Task<T> Get<T>(string path) => Default.Get<T>(path);
        public Task<long> CurrentRevision() => Default.CurrentRevision();
        public Task WaitForEndpoints(long revision, params string[] names) => Default.WaitForEndpoints(revision, names);
        public Task WaitForPublisher(Type eventType, string publisher) => Default.WaitForPublisher(eventType, publisher);

        public static async Task WaitUntil(Func<Task<bool>> condition, string description, int timeoutSeconds = 30)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (await condition().ConfigureAwait(false))
                {
                    return;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            throw new TimeoutException($"Timed out waiting for {description}.");
        }

        static async Task ResetSqlServerDatabase()
        {
            var builder = new SqlConnectionStringBuilder(sqlServerConnectionString);
            var database = builder.InitialCatalog;
            builder.InitialCatalog = "master";

            try
            {
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync().ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = $"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (SqlException e)
            {
                NUnit.Framework.Assert.Ignore($"SQL Server is not available: {e.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var endpoint in endpoints)
            {
                await endpoint.StopAsync().ConfigureAwait(false);
                endpoint.Dispose();
            }

            foreach (var router in routers)
            {
                await router.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                Directory.Delete(rootDirectory, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A running router and a client for its API.
    /// </summary>
    class TestRouter : IAsyncDisposable
    {
        readonly WebApplication application;

        public TestRouter(string name, WebApplication application)
        {
            this.application = application;
            Name = name;
            Url = new Uri(application.Urls.First());
            Api = new HttpClient { BaseAddress = Url };
        }

        public string Name { get; }
        public Uri Url { get; }
        public HttpClient Api { get; }

        public async Task<long> Put(string path, object body)
        {
            var response = await Api.PutAsJsonAsync($"api/v1/{path}", body, RoutingJson.Options).ConfigureAwait(false);
            await EnsureSuccess(response).ConfigureAwait(false);
            return await CurrentRevision().ConfigureAwait(false);
        }

        public async Task<long> Post(string path, object body)
        {
            var response = await Api.PostAsJsonAsync($"api/v1/{path}", body, RoutingJson.Options).ConfigureAwait(false);
            await EnsureSuccess(response).ConfigureAwait(false);
            return await CurrentRevision().ConfigureAwait(false);
        }

        public Task<T> Get<T>(string path) => Api.GetFromJsonAsync<T>($"api/v1/{path}", RoutingJson.Options);

        public async Task<long> CurrentRevision()
        {
            var document = await Get<RoutingDocument>("routing-document").ConfigureAwait(false);
            return document.Revision;
        }

        /// <summary>
        /// Waits until the given endpoints have reported, and then until all their instances run at least the given document revision.
        /// </summary>
        public Task WaitForEndpoints(long revision, params string[] names)
        {
            return RoutingTestEnvironment.WaitUntil(async () =>
            {
                var views = await Get<List<EndpointView>>("endpoints").ConfigureAwait(false);
                return names.All(n => views.Any(v => v.Name == n
                                                     && v.Instances.Count > 0
                                                     && v.Instances.All(i => i.Status == InstanceStatus.Live && i.DocumentRevision >= revision)));
            }, $"endpoints {string.Join(", ", names)} of router {Name} to run revision {revision}");
        }

        /// <summary>
        /// Waits until the router assigns the publisher that declared the event.
        /// </summary>
        public Task WaitForPublisher(Type eventType, string publisher)
        {
            return RoutingTestEnvironment.WaitUntil(async () =>
            {
                var routes = await Get<List<RouteView>>("routes").ConfigureAwait(false);
                return routes.Any(r => r.MessageType == eventType.FullName && r.Publisher == publisher);
            }, $"router {Name} to assign {publisher} as the publisher of {eventType.Name}");
        }

        static async Task EnsureSuccess(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {content}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            Api.Dispose();
            await application.StopAsync().ConfigureAwait(false);
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
