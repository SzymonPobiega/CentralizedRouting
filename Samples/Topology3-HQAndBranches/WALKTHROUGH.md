# Topology 3: HQ and branches

HQ runs on Azure Service Bus. Each branch runs a copy of the same branch system, a Warehouse and a Backorder service, on its own RabbitMQ virtual host. Every branch has a router that owns the branch's endpoints and is also attached to HQ's site. The HQ router owns the HQ endpoints and forwards nothing. Branch routers peer with the HQ router, but not with each other.

| Site | Transport | Router | Endpoints |
| --- | --- | --- | --- |
| `hq` | Azure Service Bus | HQ, http://localhost:5090 | Website, Sales, Billing, Shipping |
| `branch-a` | RabbitMQ virtual host `branch-a` | BranchA, http://localhost:5091 | Warehouse, Backorder |
| `branch-b` | RabbitMQ virtual host `branch-b` | BranchB, http://localhost:5092 | Warehouse, Backorder |

```
At HQ, direct (as in Topology 1):
  Website ──PlaceOrder──▶ Sales ──OrderPlaced──▶ Billing ──OrderBilled──▶ Shipping ──OrderShipped──▶ Sales

HQ → branch, Explicit (Shipping names the order's branch):
  Shipping ──ShipOrder──▶ BranchA queue on hq ─▶ BranchA ─▶ Warehouse on branch-a
  Warehouse ──ShipOrderResponse──▶ BranchA ─▶ Shipping on hq

Inside each branch, PreferLocal:
  Warehouse ──Replenish──▶ Backorder of the same branch
```

## Before you start

- `AzureServiceBus_ConnectionString` set to a namespace with Manage rights. The sample creates the queues `Website`, `Sales`, `Billing`, `Shipping`, `HQ`, `BranchA`, `BranchB`, the routers' `.Control` queues and `error`.
- RabbitMQ with the management plugin on localhost, as in Topology 2:

  ```powershell
  docker run -d --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:management
  ```

  The sample creates the virtual hosts `branch-a` and `branch-b`.
- A local SQL Server default instance for the routers' stores (`CentralRoutingTopology3HQ`, `...BranchA`, `...BranchB`, created on first run). Use another server with `CentralRouting_Sample_SqlServer`.
- Open `Topology3.sln` in this folder. It contains the sample, its messages, and the CentralRouting projects including the CLI. Build it and define a CLI function per router:

  ```powershell
  dotnet build Samples\Topology3-HQAndBranches\Topology3.sln
  . Samples\Topology3-HQAndBranches\routing.ps1
  ```

## 1. Start everything

Set the eleven `Topology3.*` projects (all but `Topology3.Messages`) as startup projects and press F5. Endpoints wait for their router if it isn't up yet. Or run `Samples\Topology3-HQAndBranches\run.ps1`: it builds the solution and opens two Windows Terminal tabs, HQ with a pane for the CLI, and Branches with a column per branch. Ctrl+Tab switches between them.

- The HQ router logs that it is attached to `hq`. Each branch router logs two lines, for its branch site through interface `local` and for `hq` through interface `hq`.
- Until the branch routers have created their queues, the HQ router warns `Could not send the snapshot to peer BranchA` (the `.Control` queue doesn't exist yet). It retries on the next peer heartbeat.
- The HQ router assigns the publishers of `OrderPlaced` (Sales), `OrderBilled` (Billing) and `OrderShipped` (Shipping).

Within a minute the routers have exchanged snapshots:

```powershell
hq peers list               # BranchA and BranchB, Online, each with its branch site and hq
hq endpoints list           # Warehouse and Backorder: owners BranchA, BranchB, on sites branch-a and branch-b
hq endpoints show Warehouse # one queue on each branch site, capable of ShipOrder on both
branchA endpoints list      # its own Warehouse and Backorder, plus the four HQ endpoints learned from HQ
```

HQ sees one logical Warehouse with an address in every branch. A branch router knows only its own branch's endpoints and HQ's. The HQ router doesn't pass on what it learned, so BranchA never hears about anything in branch B.

## 2. Route the HQ flow

```powershell
hq routes set-destination Messages.PlaceOrder Sales
hq routes subscribe Messages.OrderPlaced Billing
hq routes subscribe Messages.OrderBilled Shipping
hq routes subscribe Messages.OrderShipped Sales
```

All four are `Valid`. Now route `ShipOrder` to the Warehouse:

```powershell
hq routes set-destination Messages.ShipOrder Warehouse
```

```
Status   Warning
Warning  MultipleSites  Endpoint 'Warehouse' lives on sites branch-a, branch-b but uses the Strict address policy.
```

Each branch's Warehouse is a different business destination, so a sender that doesn't say which one it means has made a mistake. That's the Explicit policy:

```powershell
hq endpoints set-policy Warehouse Explicit
hq routes show Messages.ShipOrder   # Valid
```

Shipping now has to name the site. It takes it from the order:

```csharp
var options = new SendOptions();
options.RouteToSite(Sites.BranchSiteId(message.Branch));   // "branch-a"
return context.Send(new ShipOrder { OrderId = message.OrderId }, options);
```

A send to Warehouse without `RouteToSite` now throws in Shipping, instead of quietly going to one of the branches.

HQ talks to a branch only with commands, never with events. HQ can't subscribe Warehouse to anything, because Warehouse belongs to the branch routers:

```powershell
hq routes subscribe Messages.OrderBilled Warehouse
```

```
409 Conflict: 'Warehouse' belongs to BranchA or BranchB, so it can only subscribe to Messages.OrderBilled there. ...
```

A router that owns an endpoint on several sites refuses the subscription too, once the endpoint is Explicit: `'Warehouse' uses the Explicit address policy, so it can't subscribe to events.`

## 3. Route the branch flow

Each branch's operator routes `Replenish` on their own router, to the Backorder service, with the PreferLocal policy:

```powershell
branchA routes set-destination Messages.Replenish Backorder
branchA endpoints set-policy Backorder PreferLocal
branchB routes set-destination Messages.Replenish Backorder
branchB endpoints set-policy Backorder PreferLocal
branchA document
```

PreferLocal sends to the sender's own site whenever the endpoint has a queue there. In this sample Strict would pick the same queue, because `branchA document` lists Backorder only on `branch-a`. PreferLocal states the intent, though, and it stays right if a Backorder ever appears elsewhere. For example, if HQ ran a central Backorder, the branch routers would learn it from HQ, and Strict would warn `MultipleSites` and pick a site by name, while PreferLocal keeps every branch on its own.

## 4. Place orders

In the Website window, type `A` and press Enter, then `B`. Anything else is refused with `Unknown branch`.

| Window | Output | Path |
| --- | --- | --- |
| Website | `Sent PlaceOrder … for branch A`, `Order … accepted by Sales` | hq, direct |
| Sales | `Order … placed, shipped by branch A` | hq, direct |
| Billing | `Order … billed` | hq, direct |
| Shipping | `Shipping order … from branch A` | hq, direct |
| Warehouse (branch-a) | `Packed order … in branch A, replenishing the stock` | hq → BranchA queue on hq → branch-a |
| Backorder (branch-a) | `Branch A replenishes the stock used by order …, requested by the warehouse of branch A` | branch-a, direct |
| Shipping | `Order … shipped by branch A` | branch-a → BranchA → hq (reply) |
| Sales | `Order … shipped` | hq, direct |

The `B` order goes the same way through BranchB, and only the branch-b windows print anything. The HQ router never touches a message: Shipping sends straight to the branch router's queue on `hq`, and the reply comes back through the branch router.

## Scenario: a branch's Backorder is offline

1. Close Backorder (branch-a).
2. Type `A` in Website. The order still completes: Warehouse A packs it, Shipping gets the reply and Sales prints `Order … shipped`.
3. The `Replenish` waits in the `Backorder` queue of the `branch-a` virtual host. In the management UI (http://localhost:15672) it holds one message, and Backorder (branch-b) prints nothing. The branch-a Backorder keeps its last known queue, so the message waits for it instead of going somewhere else.
4. After a few missed heartbeats, `branchA routes show Messages.Replenish` shows `Warning  Offline  Endpoint 'Backorder' has no live instances.`
5. Start Backorder (branch-a) again. It prints `Branch A replenishes the stock used by order …`.

## Reset

Stop everything, then:

```powershell
sqlcmd -S . -E -Q "DROP DATABASE IF EXISTS CentralRoutingTopology3HQ; DROP DATABASE IF EXISTS CentralRoutingTopology3BranchA; DROP DATABASE IF EXISTS CentralRoutingTopology3BranchB;"
Remove-Item -Recurse -Force "$env:TEMP\CentralRoutingSamples\Topology3"
```

To remove the virtual hosts too: `docker exec rabbitmq rabbitmqctl delete_vhost branch-a` (and `branch-b`).
