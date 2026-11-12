# Central routing for NServiceBus

Implementation of the [design document](https://claude.ai/artifact/XTZmvdT2MGaFqph2UtyA9b). Endpoints report their capabilities to a router, operators assign routes through the router's HTTP API, and endpoints route by the routing document the router publishes.

## Projects

| Project | Purpose |
| --- | --- |
| `NServiceBus.Community.Routing.Model` | Routing document, control messages, headers and the `AddressResolver` that endpoints and routers both use to apply address policies |
| `NServiceBus.Community.Routing.Router` | The router: raw transport interfaces, control message handling, forwarding and its pipeline, peering, routing model, HTTP API, store abstraction and an in-memory store |
| `NServiceBus.Community.Routing.Router.EntityFramework` | EF Core store. Endpoints and routes are stored as JSON documents with optimistic concurrency |
| `NServiceBus.Community.Routing.Router.SqlServer` / `.PostgreSql` | `UseSqlServerStorage(...)` / `UsePostgreSqlStorage(...)` |
| `NServiceBus.Community.Routing.Router.Host` | A router configured from settings and environment variables, published as a container image |
| `NServiceBus.Community.Routing.Cli` | `routing` command line tool for the router API |
| `NServiceBus.Community.Routing` | Endpoint plugin and the configuration provider that fetches and caches the routing document |
| `NServiceBus.Community.Routing.Tests` | Unit tests for address policies, the capability model and the store contract |
| `NServiceBus.Community.Routing.AcceptanceTests` | End-to-end tests: one router, two learning-transport sites, real endpoints |
| `Rehearsal/Booth` | The conference booth demo's sites and routers in Docker, with scripts for its failure scenarios. Not part of `NServiceBus.Community.Routing.sln` |
| `Samples` | One folder per deployment topology, each with its own solution, routers, endpoints, messages and walkthrough. Not part of `NServiceBus.Community.Routing.sln` |

## Router

```csharp
var router = new RouterConfiguration("Router");
router.AddInterface("a", new LearningTransport { StorageDirectory = siteA }).Site = "site-a";
router.AddInterface("b", new LearningTransport { StorageDirectory = siteB }).Site = "site-b";
router.UseSqlServerStorage(connectionString); // or UsePostgreSqlStorage, or the default in-memory store

builder.Services.AddCentralRouter(router);
var app = builder.Build();
app.MapCentralRoutingApi();
```

The tables are created on startup if they don't exist (`EnsureCreated`; no migrations yet). A database created by an earlier version of the router has to be dropped first.

### Peering

Each router owns the endpoints that report to it and learns the others from its peers' snapshots. A snapshot is complete: what a router owns (sites, endpoints with their addresses and capabilities, published events) plus its subscription requests and grants. It is resent on every change and every `PeerHeartbeatInterval`.

```csharp
// Through a site both routers are attached to: the snapshot goes to the peer's {Name}.Control queue, gzipped and split into parts
branch.AddPeer("HQ", interfaceName: "hq");

// Over HTTP: the branch calls HQ, one call exchanges both snapshots
branch.AddHttpPeer("HQ", new Uri("https://hq-router"));
hq.AddInboundPeer("Branch");
```

Learned endpoints appear in the routing document. Senders on a site the peer is attached to send straight to the peer's queue there; otherwise their own router forwards to the peer. Routers never re-export what they learned.

### Subscriptions across routers

Subscriptions belong to the subscriber's router, the single publisher of an event to the publisher's router. Across routers it takes two steps:

1. `POST /api/v1/subscription-requests { "messageType": "..." }` on the subscriber's router. It goes to the peer that publishes the event.
2. The publisher's router grants it automatically: its next document tells the publisher to send one copy to the router (`forward: true`), and once every live publisher instance runs that revision the grant appears in its snapshot.
3. Only then does `POST /api/v1/routes/{type}/subscribers` succeed on the subscriber's router (`409` before). Adding an endpoint that belongs to a peer is always a `409`, and validation reports a stored one as `PeerSubscriber` (Invalid): a router delivers events to its own endpoints and to peers, never to a peer's endpoints. `DELETE /api/v1/subscription-requests/{type}` withdraws the request once no subscribers are left.

### Unreachable sites

A site that can't be reached when the router starts doesn't stop it: the router starts with its other interfaces and tries to connect the missing one every `InterfaceReconnectInterval` (10 seconds). Until it connects, the routing document gives the router's address on that site as its queue name (the router's name), or as `InterfaceConfiguration.RouterAddress` when set. Every transport accepts the queue name from an endpoint on the same broker or database, unless the router's queue lives in a non-default SQL schema.

A site that is down holds up only the messages for that site. The router forwards each received message to each destination site separately, running the forwarding behaviors once per site. A site that fails is retried a few times within about a second, on its own, without sending again to the others. If it still fails, or is already known to be down, its copies move to a holding queue for that site, `{Router}.Pending.{site}`, on the site the message came from, in the receive's transaction; the received message then completes. A site's copies of one message are held together as one message, whose `CentralRouting.HeldFor` header lists their queues.

Each holding queue has its own receiver, one message at a time. When it can't send a held message, the router sends a probe to its own control queue on the destination site. If the probe fails as well, the site is down: the message waits and is retried every `UnavailableSiteRetryInterval` (5 seconds). If the probe succeeds, the failure is the message's own, and after the immediate retries it moves to the error queue, from which it can be retried into the holding queue. Held messages can arrive after newer ones sent once the site is back.

`routing sites` shows each site as `Connected`, `Unreachable` (the last dispatch or probe failed) or `Connecting` (not connected since the router started). Receive-side outages, such as losing the connection to a database the router reads from, are handled by the transport and are not reflected in that status.

### Forwarding behaviors

```csharp
router.AddForwardingBehavior(new MyBehavior());   // or AddForwardingBehavior<MyBehavior>() to create it from DI

class MyBehavior : IForwardingBehavior
{
    public async Task Invoke(ForwardingContext context, Func<Task> next)
    {
        // Runs once per destination site. context.Messages: the copies for that site; change, add or remove them
        await next();   // dispatches; skipping it drops the message
        // runs after dispatch, before the received message is acknowledged
    }
}
```

`context.GetDispatchTransaction(site)` is the transaction each site's messages are dispatched with, so a behavior can put a connection and transaction in it and commit after `next`.

`NServiceBus.Community.Routing.Router.SqlServer` has one such behavior. It forwards each message into a SQL Server transport site at most once, which keeps that site's exactly-once processing when messages reach it over an at-least-once transport such as a backplane:

```csharp
router.AddSqlServerDeduplication("billing", connectionString);   // table dbo.RouterDeduplication (MessageId, InsertedAt), created on first use
```

The message ID is inserted in the transaction the messages are sent in, so a message is forwarded and recorded, or neither. Rows are never removed.

Storage goes through `IRouterStore`. It works with whole documents (an endpoint with all its instances, a route) and a revision counter, so a document database implementation only needs those operations.

## Router container

`NServiceBus.Community.Routing.Router.Host` is a ready-made router configured without code, and the image CI pushes to `ghcr.io/szymonpobiega/nservicebus-community-router`, for linux-x64 and linux-arm64 (such as a Raspberry Pi). It includes the SQL Server, PostgreSQL, RabbitMQ, Azure Service Bus and learning transports. Settings are read from the `Router` section of `appsettings.json`, from environment variables (levels separated by `__`), or from secret files: each file in `/run/secrets` (or `SecretsDirectory`) is one setting named like the environment variable, such as `Router__Storage__ConnectionString`.

```bash
docker run -p 8080:8080 \
  -e Router__Name=Branch042 \
  -e Router__Storage__Type=SqlServer \
  -e Router__Storage__ConnectionString="Server=sql;Database=Router;..." \
  -e Router__Interfaces__local__Type=RabbitMQ \
  -e Router__Interfaces__local__ConnectionString="host=rabbit;virtualHost=branch042" \
  -e Router__Interfaces__local__Site=branch-042 \
  -e Router__Interfaces__hq__Type=AzureServiceBus \
  -e Router__Interfaces__hq__ConnectionString="Endpoint=sb://..." \
  -e Router__Interfaces__hq__Site=hq \
  -e Router__Peers__HQ__Interface=hq \
  ghcr.io/szymonpobiega/nservicebus-community-router
```

| Setting | Values |
| --- | --- |
| `Router__Name` | Required |
| `Router__HeartbeatInterval`, `RefreshInterval`, `PeerHeartbeatInterval`, `InterfaceReconnectInterval`, `UnavailableSiteRetryInterval` | `hh:mm:ss`; also `MissedHeartbeatsBeforeStale`, `MissedPeerHeartbeatsBeforeOffline`, `MaxHops`, `ImmediateRetries` |
| `Router__Storage__Type`, `ConnectionString` | `InMemory` (default), `SqlServer` or `PostgreSql` |
| `Router__Interfaces__{name}__Type` | `SqlServer`, `PostgreSql`, `RabbitMQ`, `AzureServiceBus` or `Learning` |
| `…__ConnectionString` | The transport's connection string; the storage directory for `Learning` |
| `…__Site` | Site ID; defaults to `{Router}.{name}` |
| `…__SiteIdentities__0`, `__1`, … | Extra identities. The router derives one from the connection string for every transport; add one when the router reaches the broker by another host name than the endpoints, such as a Docker service name |
| `…__RouterAddress` | The router's queue as endpoints on the site address it, used until the interface first connects; defaults to the router's name |
| `…__TransactionMode` | Defaults to `SendsAtomicWithReceive` for SQL Server and PostgreSQL, the transport's default otherwise |
| `…__Schema`, `QueueType`, `ErrorQueue`, `MaxSnapshotPartSize` | Schema for SQL Server and PostgreSQL, `Quorum` (default) or `Classic` for RabbitMQ |
| `…__Deduplication` | `true` to forward into a SQL Server site at most once |
| `Router__Peers__{name}__Interface`, `Url` or `Inbound` | Exactly one: a shared interface, the peer's HTTP API, or `true` when the peer calls this router |

The router stops at startup with a list of every invalid setting. The API and `/health` listen on port 8080. Forwarding behaviors other than deduplication need a host of your own, built from the packages like the samples' routers.

## Command line tool

`NServiceBus.Community.Routing.Cli` calls the router API and formats the results with Spectre.Console. It packs as a .NET tool named `routing`; during development run it with `dotnet run --project NServiceBus.Community.Routing.Cli -- <command>`.

```
routing status                                              # sites, endpoints, routes by validation status, peers
routing endpoints list | show <endpoint> | set-policy <endpoint> <policy>
routing endpoints disable-instance | enable-instance | remove-instance <endpoint> <instance>
routing endpoints disable-site | enable-site <endpoint> <site>
routing types list [--unrouted] | candidates <type>
routing routes list | show <type> | delete <type> [--yes]
routing routes set-destination | set-publisher | subscribe <type> <endpoint> [--comment <text>]
routing routes unsubscribe <type> <endpoint>
routing routes migrate-publisher <type> <endpoint> | complete-migration <type> | rollback-migration <type>
routing routes validate <type> --destination <endpoint> | --publisher <endpoint> --subscriber <endpoint>...
routing peers list | show <peer>
routing subscriptions list | request <type> [--wait] | withdraw <type>
routing sites
routing document                                            # the routing document as a tree
```

The router URL comes from `--router <url>` (after the command), the `CENTRALROUTING_ROUTER` environment variable, or defaults to `http://localhost:5080`. `--json` prints the API response instead, uncolored when piped.

## Endpoint
```csharp
builder.Configuration.AddCentralRouting(o =>
{
    o.RouterUrl = new Uri("http://localhost:5080");
    o.Cache = RoutingDocumentCache.FileSystem(cacheDirectory);
});

endpointConfiguration.UseCentralRouting("Router", builder.Configuration);
```

Declare the events the endpoint publishes:

```csharp
endpointConfiguration.UseCentralRouting("Router", builder.Configuration).Publishes<OrderPlaced>();
```

The declaration is part of the capability report. When exactly one endpoint declares an event that has no publisher yet, the router assigns it as the publisher (`changedBy` is the router's name). When several endpoints declare it, when a peer already publishes it, or when this router requested it from a peer, the operator decides with `PUT /api/v1/routes/{type}/publisher`; validation reports the conflict (`PublisherConflict`) and a publisher that does not declare the event (`NotDeclared`). An endpoint throws when it publishes an event it does not declare, and when it publishes an event assigned to another endpoint.

Subscribers are assigned to the type that is published, never to its base types. A handler of `IOrderEvent` makes an endpoint capable of `OrderPlaced`, so `routing types candidates Messages.OrderPlaced` lists it, but it receives `OrderPlaced` only once it subscribes to `OrderPlaced` itself. A new derived event reaches nobody until its subscribers are assigned.

### Migrating a publisher

To replace a publisher, deploy the new endpoint next to the old one (both declare the event) and start a migration. Until it is completed, both may publish; subscribers are not affected.

```
routing routes migrate-publisher Messages.OrderPlaced BillingV2    # POST   /api/v1/routes/{type}/publisher/migration
routing routes complete-migration Messages.OrderPlaced             # DELETE /api/v1/routes/{type}/publisher/migration
routing routes rollback-migration Messages.OrderPlaced             # POST   /api/v1/routes/{type}/publisher/migration/rollback
```

While the migration is open, validation reports `PublisherMigration` and says when the old publisher has no live instances left. Completing is always explicit, because an endpoint that is restarting looks the same as one that is gone. Both endpoints must be owned by the same router, and if both handle the same input during the overlap, subscribers get the event twice.

`UseCentralRouting("Router", routerUrl)` is a shortcut that builds the configuration internally. The site identity is detected for the learning transport; for other transports set `SiteIdentity`. Send to a specific site with `options.RouteToSite("site-b")`; an endpoint with the Explicit policy requires it. Explicit endpoints, such as one per branch, can't subscribe to events: the router refuses the subscription, and refuses to make a subscriber Explicit. Events don't cross between such sites; senders address one site with a command.

## Samples

All samples run the same business flow: Website sends `PlaceOrder` to Sales, Sales replies and publishes `OrderPlaced`, Billing publishes `OrderBilled`, Shipping publishes `OrderShipped`, and Sales subscribes to it. In Topology 2 and the backplane, Billing first charges through PaymentGateway (`Charge`/`ChargeResponse`) and Shipping registers a parcel with ParcelDelivery (`RegisterParcel`/`RegisterParcelResponse`). In Topology 3, Shipping ships each order from the Warehouse of the branch the customer picked, and that Warehouse sends `Replenish` to its own branch's Backorder service.

Each sample is self-contained: its folder has a solution with the sample's projects, a `Messages` project with only the messages it uses, and its helper code in `Shared`. The solution also includes the CentralRouting projects it references, and the CLI the walkthrough uses. Instead of starting the projects from Visual Studio, `run.ps1` in the sample's folder builds the solution and runs every process in its own pane of a Windows Terminal tab, next to a pane with the CLI functions from `routing.ps1`.

| Sample | Solution | Sites | Walkthrough |
| --- | --- | --- | --- |
| Topology 1: one router, two sites | `Topology1.sln` | SQL Server (Website, Sales) and Azure Service Bus (Billing, Shipping) | [WALKTHROUGH.md](Samples/Topology1-OneRouterTwoSites/WALKTHROUGH.md) |
| Topology 2: one router, a site per service | `Topology2.sln` | A RabbitMQ virtual host per service: sales, billing, shipping | [WALKTHROUGH.md](Samples/Topology2-SitePerService/WALKTHROUGH.md) |
| Topology 3: HQ and branches | `Topology3.sln` | Azure Service Bus at HQ (Website, Sales, Billing, Shipping) and a RabbitMQ virtual host per branch (Warehouse, Backorder), a router per branch | [WALKTHROUGH.md](Samples/Topology3-HQAndBranches/WALKTHROUGH.md) |
| Topology 4: backplane | `Topology4.sln` | A SQL Server database and router per part, routers peered over Azure Service Bus | [WALKTHROUGH.md](Samples/Topology4-Backplane/WALKTHROUGH.md) |
| Topology 5: migration between sites | `Topology5.sln` | Starts with every endpoint on RabbitMQ and moves them to Azure Service Bus one at a time with the NewestSite policy | [WALKTHROUGH.md](Samples/Topology5-Migration/WALKTHROUGH.md) |

## Build and tests

```
dotnet build
dotnet test
```

Every build of a library project, and of the CLI, also creates its NuGet package in `artifacts/packages`. The version comes from the latest `v*` git tag through MinVer: a tagged commit gets that version (`v0.2.0` → `0.2.0`), later commits a pre-release of the next patch, and a repository with no tags `0.0.0-alpha.0`. GitHub Actions (`.github/workflows/ci.yml`) builds, tests against a SQL Server container, builds the samples, and uploads the packages as an artifact. It then builds the router image and, on `master` and tags, pushes it to ghcr.io. To build the image locally: `dotnet publish NServiceBus.Community.Routing.Router.Host -c Release -t:PublishContainer` (needs Docker, or add `-p:ContainerArchiveOutputPath=router.tar.gz`).

Tests that use SQL Server connect to the local default instance with integrated security. Override the connection string with the `CentralRouting_SqlServer_ConnectionString` environment variable. Tests are ignored when SQL Server is not reachable.

## Scope of this version

Implemented: capability reports and heartbeats, site identity learning, capability model, routes and validation, all five address policies, disabling instances and sites, routing document with ETag, configuration provider with file cache and reload (on by default), cross-site forwarding of commands, replies and events (one copy per hop, fan-out at the router), single-publisher enforcement, published event declarations with automatic publisher assignment, publisher migrations, router peering over a shared site or HTTP, subscription requests and grants, forwarding behaviors and a SQL Server deduplication behavior.

Not yet implemented: topology plugins and multicast delivery, type aliases, S3/Blob caches, API authentication, EF migrations, the `Activating`/`Deactivating` status of local subscriptions.
