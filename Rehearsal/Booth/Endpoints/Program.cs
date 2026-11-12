using CentralRouting.Model;
using CentralRouting.NServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rehearsal;

//The booth demo's flow with central routing:
//  dotnet run -- client [--transport postgres]    ClientUI: RabbitMQ (booth site), or PostgreSQL on its own Pi (client site) in robust mode
//  dotnet run -- sales                            Sales on the booth site
//  dotnet run -- billing | shipping               Billing and Shipping on Azure Service Bus, owned by the Cloud router
var role = args.FirstOrDefault()?.ToLowerInvariant();
var builder = Host.CreateApplicationBuilder(args);
var robust = builder.Configuration["transport"] == "postgres";

var asbConnectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString")
                          ?? throw new Exception("Set AzureServiceBus_ConnectionString to the namespace the routers use.");

var endpoint = role switch
{
    "client" when robust => new EndpointSetup("BoothClient", Routers.Booth, new PostgreSqlTransport(Sites.ClientDatabase), "client"),
    "client" => new EndpointSetup("BoothClient", Routers.Booth, Sites.Rabbit(), "booth"),
    "sales" => new EndpointSetup("BoothSales", Routers.Booth, Sites.Rabbit(), "booth", r => r.Publishes<OrderPlaced>()),
    "billing" => new EndpointSetup("BoothBilling", Routers.Cloud, Sites.Azure(asbConnectionString), SiteIdentity.ForAzureServiceBus(asbConnectionString), r => r.Publishes<OrderBilled>()),
    "shipping" => new EndpointSetup("BoothShipping", Routers.Cloud, Sites.Azure(asbConnectionString), SiteIdentity.ForAzureServiceBus(asbConnectionString), r => r.Publishes<OrderShipped>()),
    _ => throw new Exception("Pass the role: client [--transport postgres], sales, billing or shipping.")
};

var instance = robust ? "postgres" : "rabbitmq";
Console.Title = role == "client" ? $"{endpoint.Name} ({instance})" : endpoint.Name;
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

var cacheDirectory = Path.Combine(Path.GetTempPath(), "CentralRoutingRehearsal", $"{endpoint.Name}-{instance}");
await Routers.WaitFor(endpoint.Router, cacheDirectory);
builder.Configuration.AddCentralRouting(o =>
{
    o.RouterUrl = endpoint.Router.Url;
    o.Cache = RoutingDocumentCache.FileSystem(cacheDirectory);
    o.ReloadInterval = TimeSpan.FromSeconds(5);
});

var configuration = new EndpointConfiguration(endpoint.Name);
configuration.UseSerialization<SystemJsonSerializer>();
configuration.UseTransport(endpoint.Transport);
configuration.EnableInstallers();
configuration.Recoverability().Delayed(d => d.NumberOfRetries(0));
configuration.AssemblyScanner().ExcludeTypes(Handlers.NotFor(role!));

var routing = configuration.UseCentralRouting(endpoint.Router.Name, builder.Configuration);
//Each site is identified by a fixed name, because the routers reach RabbitMQ and the client database by other host names than the endpoints
routing.SiteIdentity = endpoint.SiteIdentity;
routing.InstanceId = $"{Environment.MachineName}-{endpoint.Name}-{instance}";
routing.HeartbeatInterval = TimeSpan.FromSeconds(10);
endpoint.Publishes?.Invoke(routing);

builder.Services.AddNServiceBusEndpoint(configuration);
if (role == "client")
{
    builder.Services.AddHostedService<OrderGenerator>();
}

await builder.Build().RunAsync();

record EndpointSetup(string Name, Router Router, NServiceBus.Transport.TransportDefinition Transport, string SiteIdentity, Action<CentralRoutingSettings>? Publishes = null);

record Router(string Name, Uri Url);

static class Routers
{
    public static readonly Router Booth = new("BoothRouter", new Uri("http://localhost:5095"));
    public static readonly Router Cloud = new("CloudRouter", new Uri("http://localhost:5096"));

    /// <summary>
    /// Without a cached routing document an endpoint cannot start while its router is down.
    /// </summary>
    public static async Task WaitFor(Router router, string cacheDirectory)
    {
        if (Directory.Exists(cacheDirectory) && Directory.EnumerateFiles(cacheDirectory).Any())
        {
            return;
        }

        using var http = new HttpClient { BaseAddress = router.Url, Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                (await http.GetAsync("api/v1/sites")).EnsureSuccessStatusCode();
                return;
            }
            catch (Exception) when (attempt < 60)
            {
                if (attempt == 0)
                {
                    Console.WriteLine($"Waiting for {router.Name} at {router.Url}...");
                }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}

static class Sites
{
    /// <summary>
    /// The client Pi's local database, which ClientUI reaches as localhost and the Booth router as client-pi.
    /// </summary>
    public const string ClientDatabase = "Host=localhost;Port=54321;Database=booth;Username=postgres;Password=rehearsal";

    public static RabbitMQTransport Rabbit() => new(RoutingTopology.Conventional(QueueType.Quorum), "host=localhost");

    public static AzureServiceBusTransport Azure(string connectionString) => new(connectionString, TopicTopology.Default);
}

/// <summary>
/// Places an order every two seconds, like the booth's ClientUI.
/// </summary>
class OrderGenerator(IMessageSession session, ILogger<OrderGenerator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var number = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            var orderId = $"{DateTime.Now:HHmmss}-{++number}";
            try
            {
                await session.Send(new PlaceOrder { OrderId = orderId }, stoppingToken);
                Console.WriteLine($"Placed order {orderId}");
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Could not place order {OrderId}: {Error}", orderId, e.Message);
            }
        }
    }
}
