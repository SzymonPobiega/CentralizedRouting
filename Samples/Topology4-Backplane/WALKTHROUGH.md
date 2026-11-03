# Topology 4: backplane

Each part of the system runs on its own SQL Server database with its own router. An Azure Service Bus namespace, the backplane, connects the routers and carries no endpoints. Every router peers with every other router over the backplane, and each part's operators manage only their own router.

| Part (site) | Database | Router | Endpoints |
| --- | --- | --- | --- |
| `sales` | `CentralRoutingTopology4Sales` | SalesRouter, http://localhost:5081 | Website, Sales |
| `billing` | `CentralRoutingTopology4Billing` | BillingRouter, http://localhost:5082 | Billing, PaymentGateway |
| `shipping` | `CentralRoutingTopology4Shipping` | ShippingRouter, http://localhost:5083 | Shipping, ParcelDelivery |
| `backplane` | Azure Service Bus | all three | none |

```
Inside each part, direct (command and reply):
  sales:     Website  ──PlaceOrder──────▶ Sales
  billing:   Billing  ──Charge──────────▶ PaymentGateway
  shipping:  Shipping ──RegisterParcel──▶ ParcelDelivery

Between parts, through two routers and the backplane (events):
  Sales ─▶ SalesRouter ═▶ BillingRouter ─▶ Billing          OrderPlaced
  Billing ─▶ BillingRouter ═▶ ShippingRouter ─▶ Shipping    OrderBilled
  Shipping ─▶ ShippingRouter ═▶ SalesRouter ─▶ Sales        OrderShipped
```

Each router's store lives in its part's database, next to the transport's tables.

The endpoints in each part process messages exactly once: the SQL Server transport receives a message in a transaction that the handler's business data changes can share. The backplane delivers at least once, though, so a message can arrive at a part's router twice. Each router therefore deduplicates what it forwards into its part:

```csharp
router.AddSqlServerDeduplication(part.SiteId, part.ConnectionString);
```

The router inserts the message ID into `dbo.RouterDeduplication` in the part's database, in the same transaction it uses to send the message to the endpoint's queue. A message whose ID is already there is dropped.

## Before you start

- A local SQL Server default instance with integrated security. Point the sample at another server with `CentralRouting_Sample_SqlServer`. The three databases are created on first run.
- `AzureServiceBus_ConnectionString` set to a namespace with Manage rights. The sample creates the queues `SalesRouter`, `BillingRouter`, `ShippingRouter`, their `.Control` queues and `error`.
- Open `Topology4.sln` in this folder. It contains the sample, its messages, and the CentralRouting projects including the CLI. Build it and define a CLI function per router:

  ```powershell
  dotnet build Samples\Topology4-Backplane\Topology4.sln
  . Samples\Topology4-Backplane\routing.ps1
  ```

## 1. Start everything

Set the nine `Topology4.*` projects (all but `Topology4.Messages`) as startup projects and press F5. Endpoints wait for their router if it isn't up yet. Or run `Samples\Topology4-Backplane\run.ps1`: it builds the solution and opens a Windows Terminal tab with a column per part, router on top, and a pane with the CLI.

- Each router logs that it is attached to its part's site through interface `local` and to `backplane` through interface `backplane`.
- Each router assigns its own part's publisher: SalesRouter `OrderPlaced` to Sales, BillingRouter `OrderBilled` to Billing, ShippingRouter `OrderShipped` to Shipping.

Within a minute the routers have exchanged snapshots:

```powershell
billing peers list        # SalesRouter and ShippingRouter, Online, each with its sites and published events
billing endpoints list    # Billing and PaymentGateway owned by BillingRouter; the other four learned
sales document
```

The SalesRouter document shows how Website and Sales reach the rest. From the `sales` site, every other site goes via the `SalesRouter` queue in the Sales database. From `backplane`, `billing` goes via `BillingRouter` and `shipping` via `ShippingRouter`.

## 2. Route the commands inside each part

Each part's operator routes their own commands on their own router:

```powershell
sales    routes set-destination Messages.PlaceOrder Sales
billing  routes set-destination Messages.Charge PaymentGateway
shipping routes set-destination Messages.RegisterParcel ParcelDelivery
```

## 3. Subscribe to another part's events

A subscription belongs to the subscriber's router. That router can add subscribers only when it knows the publisher is available, so first try without:

```powershell
billing routes subscribe Messages.OrderPlaced Billing
```

```
409 Conflict: No publisher of Messages.OrderPlaced is available. Assign one with PUT /api/v1/routes/{type}/publisher, or request a subscription from the router that owns the publisher.
```

Request the event from the router that owns its publisher, then subscribe:

```powershell
billing subscriptions request Messages.OrderPlaced --wait
billing routes subscribe Messages.OrderPlaced Billing
```

The request takes up to a minute:
1. BillingRouter sends the request to SalesRouter in its snapshot.
2. SalesRouter grants it automatically. Its next routing document tells Sales to send a copy of `OrderPlaced` to SalesRouter.
3. Once Sales reports that it runs that document, the grant goes back to BillingRouter in SalesRouter's snapshot.

This way no event is lost between subscribing and the publisher starting to forward.

Subscribing on the publisher's router doesn't work either, because SalesRouter delivers events only to its own endpoints and to peers holding a grant:

```powershell
sales routes subscribe Messages.OrderPlaced Billing
```

```
409 Conflict: 'Billing' belongs to BillingRouter, so it can only subscribe to Messages.OrderPlaced there. Request the event on BillingRouter and add the subscriber there.
```

Do the same for the other two events:

```powershell
shipping subscriptions request Messages.OrderBilled --wait
shipping routes subscribe Messages.OrderBilled Shipping
sales subscriptions request Messages.OrderShipped --wait
sales routes subscribe Messages.OrderShipped Sales
sales subscriptions list    # outgoing OrderShipped to ShippingRouter, incoming OrderPlaced from BillingRouter, both Granted
```

In `sales document`, `OrderPlaced` is now marked "forwarded to peers". `OrderShipped` has Sales as a subscriber and no local publisher.

## 4. Place an order

Press Enter in Website:

| Window | Output | Path |
| --- | --- | --- |
| Website | `Sent PlaceOrder …`, `Order … accepted by Sales` | sales, direct |
| Sales | `Order … placed` | sales, direct |
| Billing | `Charging order …` | SalesRouter → backplane → BillingRouter |
| PaymentGateway | `Charged the card for order …` | billing, direct |
| Billing | `Order … billed` | billing, direct (reply) |
| Shipping | `Registering a parcel for order …` | BillingRouter → backplane → ShippingRouter |
| ParcelDelivery | `Registered parcel PD… for order …` | shipping, direct |
| Shipping | `Order … shipped, tracking number PD…` | shipping, direct (reply) |
| Sales | `Order … shipped` | ShippingRouter → backplane → SalesRouter |

Each event crosses three segments: the publisher's site, the backplane and the subscriber's site. Each router delivers on its own part. SalesRouter never needs to know where Billing's queue is inside the billing database.

## Scenario: a part's router goes down

1. Close the BillingRouter window.
2. Press Enter in Website. Sales places the order and SalesRouter forwards `OrderPlaced` to BillingRouter's queue on the backplane, where it waits.
3. After three missed peer heartbeats, about 90 seconds, `sales peers list` shows BillingRouter as `Offline`. SalesRouter keeps BillingRouter's last snapshot and keeps forwarding to its queue.
4. Start BillingRouter again. It works through its backplane queue, and the order completes all the way back to Sales.

## Scenario: the backplane delivers messages twice

Each router has two switches, set as environment variables or as arguments (`--CentralRouting_Sample_Deduplication false`):

| Setting | Default | Effect |
| --- | --- | --- |
| `CentralRouting_Sample_DuplicateOnBackplane` | `false` | A forwarding behavior sends every message going onto the backplane a second time, with 50% probability, under the same message ID |
| `CentralRouting_Sample_Deduplication` | `true` | The router deduplicates what it forwards into its part |

The router projects have a launch profile for each mode: the default, `Duplicating backplane` and `Duplicating backplane, no deduplication`. `run.ps1 -Backplane Duplicating` and `run.ps1 -Backplane NoDeduplication` start the routers with the last two. To switch while the sample runs, press Ctrl+C in each router pane and start it again, e.g. `dotnet run --no-build --launch-profile 'Duplicating backplane'`. Each router logs its mode at startup, e.g. `Duplicating messages sent over the backplane: on. Deduplicating messages forwarded into billing: on`.

**With deduplication.** Start the three routers with the `Duplicating backplane` profile and place six orders. The routers warn `Sending message … over the backplane twice` for about half the events, and the receiving router logs `Message … was already forwarded to site billing. Dropping the duplicate` for each one. Every order is charged, registered and shipped once. In one run, 8 of the 18 events went over the backplane twice, and all 8 duplicates were dropped.

**Without deduplication.** Restart the routers with `Duplicating backplane, no deduplication` and place six more orders. Each duplicate is processed again, and its handler publishes the next event again, which can be duplicated in turn. In one run, six orders produced 11 card charges, 16 parcel registrations and 24 `Order … shipped` lines in Sales, and Billing printed `Charging order …` twice for most orders.

## Scenario: one duplicate, step by step

Each router records the events it forwarded into its part:

```powershell
sqlcmd -S . -E -d CentralRoutingTopology4Billing -Q "SELECT MessageId, InsertedAt FROM dbo.RouterDeduplication"
```

One row per `OrderPlaced` so far. To create a duplicate:

1. Close the BillingRouter window.
2. Press Enter in Website. `OrderPlaced` waits in BillingRouter's queue on the backplane.
3. Copy the waiting message, as the backplane might after a lost acknowledgement:

   ```powershell
   dotnet run Samples\Topology4-Backplane\duplicate.cs BillingRouter
   ```

   ```
   Duplicated 1c4216e9-… (Messages.OrderPlaced, …) in BillingRouter
   ```

4. Start BillingRouter again. It forwards the first copy and logs for the second:

   ```
   Message 1c4216e9-… was already forwarded to site billing. Dropping the duplicate
   ```

Billing prints `Charging order …` once, and PaymentGateway charges the card once.

The deduplication data is never cleaned up. `InsertedAt` lets a job remove rows older than any redelivery could be.

## Reset

Stop everything, then:

```powershell
sqlcmd -S . -E -Q "DROP DATABASE IF EXISTS CentralRoutingTopology4Sales; DROP DATABASE IF EXISTS CentralRoutingTopology4Billing; DROP DATABASE IF EXISTS CentralRoutingTopology4Shipping;"
Remove-Item -Recurse -Force "$env:TEMP\CentralRoutingSamples\Topology4"
```
