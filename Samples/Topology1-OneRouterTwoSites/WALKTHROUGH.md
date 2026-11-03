# Topology 1: one router, two sites

One router owns two sites and forwards everything that crosses between them. Every endpoint reports to it and all routes are assigned on it.

| Site | Broker | Endpoints |
| --- | --- | --- |
| `on-premises` | SQL Server transport, database `CentralRoutingTopology1` on the local instance | Website, Sales |
| `azure` | Azure Service Bus, the namespace in `AzureServiceBus_ConnectionString` | Billing, Shipping |

```
Website ──PlaceOrder──▶ Sales ──OrderPlaced──▶ [Router] ──▶ Billing ──OrderBilled──▶ Shipping
   ◀──PlaceOrderResponse──┘  ◀──────────────── [Router] ◀──OrderShipped─────────────────┘
```

Website and Sales talk directly over SQL Server, and so do Billing and Shipping over Azure Service Bus. Only `OrderPlaced` and `OrderShipped` cross sites, through the router.

## Before you start

- A local SQL Server default instance (`.`) with integrated security. The databases are created on first run: `CentralRoutingTopology1` for the transport and `CentralRoutingTopology1Router` for the router's store. Override the transport connection string with `CentralRouting_Sample_SqlServerTransport` and the store's in `Router/appsettings.json`.
- `AzureServiceBus_ConnectionString` set to a namespace with Manage rights. The sample creates the queues `Router`, `Router.Control`, `Billing`, `Shipping` and `error`, plus the transport's topic.
- Open `Topology1.sln` in this folder. It contains the sample, its messages, and the CentralRouting projects including the CLI. To build it and make the CLI easy to call, in PowerShell from the repository root:

  ```powershell
  dotnet build Samples\Topology1-OneRouterTwoSites\Topology1.sln
  . Samples\Topology1-OneRouterTwoSites\routing.ps1
  ```

## 1. Start everything

Set **Topology1.Router, Topology1.Website, Topology1.Sales, Topology1.Billing and Topology1.Shipping** as startup projects and press F5, or `dotnet run` each project. The order doesn't matter: endpoints print `Waiting for the router at http://localhost:5080/...` until the router is up.

Or run `Samples\Topology1-OneRouterTwoSites\run.ps1`. It builds the solution and opens a Windows Terminal tab with a pane per process, a row per site, and a pane with the CLI ready to use.

What you should see:
- The router logs `Router Router attached to site on-premises through interface sql` and the same for `azure`.
- Each endpoint logs `Using routing document revision N from router Router. Local site: on-premises` (or `azure`). The endpoint found its site by matching its connection's identity against the routing document. No site name is configured on the endpoints.
- The router logs `Assigned Sales as the publisher of Messages.OrderPlaced`, and the same for `OrderBilled` (Billing) and `OrderShipped` (Shipping). Each endpoint declares what it publishes with `routing.Publishes<T>()`, and the router assigns an event to the only endpoint that declares it.

## 2. Look around

```powershell
routing status
routing sites
routing endpoints list
routing types list
```

- `sites` lists the two sites with their identities: `sqlserver:v1:./centralroutingtopology1/dbo` and `asb:v1:<your-namespace>.servicebus.windows.net`.
- `endpoints list` shows the four endpoints with one live instance each.
- `types list` shows what the endpoints handle and publish. The three events already have an event route, and "Declared by" names their publishers. `PlaceOrder` is unrouted. `PlaceOrderResponse` is a reply and never needs a route.

## 3. Send a command without a route

Press Enter in the Website window. You should see:

```
Could not send PlaceOrder 9645a1e7: No route for Messages.PlaceOrder. Assign a destination with the router's API.
```

Nothing is sent. The endpoint refuses to guess where a command goes.

## 4. Route the command

```powershell
routing types candidates Messages.PlaceOrder      # Sales, on-premises: Capable
routing routes set-destination Messages.PlaceOrder Sales
```

Endpoints reload the routing document every 5 seconds. Press Enter in Website again:

- Website: `Sent PlaceOrder …`, then `Order … accepted by Sales`.
- Sales: `Order … placed`.
- Billing stays silent: `OrderPlaced` has no subscribers yet, so Sales publishes it to nobody.

Neither message touched the router: Website and Sales are on the same site.

## 5. Subscribe across sites

```powershell
routing routes subscribe Messages.OrderPlaced Billing
routing routes subscribe Messages.OrderBilled Shipping
routing routes subscribe Messages.OrderShipped Sales
routing routes list
routing document
```

- `routes list` shows four routes, all `Valid`.
- `document` shows what the endpoints route by. Under Sites, `on-premises` reaches `azure` via the `Router` queue and vice versa. Under Events, each event has its publisher and subscribers.

Press Enter in Website. Within a few seconds:

| Window | Output | Hop |
| --- | --- | --- |
| Sales | `Order … placed` | Website → Sales, SQL Server, direct |
| Billing | `Order … billed` | Sales → Router → Billing, SQL Server to Azure Service Bus |
| Shipping | `Order … shipped` | Billing → Shipping, Azure Service Bus, direct |
| Sales | `Order … shipped` | Shipping → Router → Sales, Azure Service Bus to SQL Server |

Sales publishes `OrderPlaced` once, to the router's queue on its own site. The router forwards it to every subscriber on the other site.

## Scenario: a subscriber goes offline

1. Close the Shipping window.
2. `routing endpoints show Shipping`: its instance is `Stopped` (or `Stale` about 30 seconds later, if it was killed rather than shut down), and its queue is still listed.
3. `routing routes show Messages.OrderBilled`: status `Warning`, with the finding `Endpoint 'Shipping' has no live instances.`
4. Press Enter in Website a few times. Billing bills each order, and the `OrderBilled` events wait in the `Shipping` queue.
5. Start Shipping again. It ships the backlog, and Sales prints `Order … shipped` for each order.

An endpoint that is scaled to zero or restarting keeps its queues in the routing document, so nothing is lost while it's away.

## Scenario: scale out Billing

1. Start a second instance:

   ```powershell
   dotnet run --project Samples/Topology1-OneRouterTwoSites/Billing -- --instance 2
   ```

   `--instance` gives it its own instance ID. Two copies started from the same folder would otherwise report as one.
2. `routing endpoints show Billing`: two live instances, and the site's queue shows `Billing ×2`. Both instances read the same queue.
3. Press Enter in Website several times. Orders are split between the two Billing windows.
4. Close the second instance. Its status turns `Stopped` (or `Stale` about 30 seconds later), and the queue weight goes back to `×1`.
5. Remove it for good:

   ```powershell
   routing endpoints remove-instance Billing <instance id from step 2>
   ```

   A stopped or stale instance only drops out on its own once it misses three heartbeats. `remove-instance` is the manual cleanup for an instance that will not come back.

## Scenario: a second publisher

1. Stop Billing and add `routing.Publishes<OrderShipped>()` to `Billing/Program.cs`. Billing now declares an event that Shipping already publishes. Start Billing again.
2. `routing routes show Messages.OrderShipped`: Shipping stays the publisher, with the warning `'Billing' also declares that it publishes Messages.OrderShipped. Only 'Shipping' may publish it. To move the event to 'Billing', migrate the publisher.`
3. If Billing published `OrderShipped` now, it would get an exception: `Its only publisher is Shipping`.
4. To hand the event over:

   ```powershell
   routing routes migrate-publisher Messages.OrderShipped Billing
   ```

   Both may publish until you run `routing routes complete-migration Messages.OrderShipped`. `routing routes rollback-migration Messages.OrderShipped` makes Shipping the only publisher again.

Remove the line from `Billing/Program.cs` afterwards.

## Reset

Stop everything, then:

```powershell
sqlcmd -S . -E -Q "DROP DATABASE IF EXISTS CentralRoutingTopology1; DROP DATABASE IF EXISTS CentralRoutingTopology1Router;"
Remove-Item -Recurse -Force "$env:TEMP\CentralRoutingSamples\Topology1"
```

The second line removes the routing documents the endpoints cached. The Azure Service Bus queues and topic can stay; delete them in the portal if you want a clean namespace.
