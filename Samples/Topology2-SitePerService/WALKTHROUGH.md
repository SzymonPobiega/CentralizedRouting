# Topology 2: one router, a site per service

Each service team gets its own RabbitMQ virtual host, and one router connects them. Routes between services always go through the router; routes inside a service stay on its virtual host.

| Site (virtual host) | Endpoints |
| --- | --- |
| `sales` | Website, Sales |
| `billing` | Billing, PaymentGateway |
| `shipping` | Shipping, ParcelDelivery |

```
Inside each service, direct (command and reply):
  sales:     Website  ──PlaceOrder──────▶ Sales
  billing:   Billing  ──Charge──────────▶ PaymentGateway
  shipping:  Shipping ──RegisterParcel──▶ ParcelDelivery

Between services, through the Router (events):
  Sales ──OrderPlaced──▶ Billing ──OrderBilled──▶ Shipping ──OrderShipped──▶ Sales
```

## Before you start

- RabbitMQ with the management plugin on localhost:

  ```powershell
  docker run -d --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:management
  ```

  The sample creates the virtual hosts `sales`, `billing` and `shipping` through the management API, as `guest`, and gives that user permissions on them. Use another broker or user with `CentralRouting_Sample_RabbitMQHost`, `CentralRouting_Sample_RabbitMQUser` and `CentralRouting_Sample_RabbitMQPassword`.
- A local SQL Server default instance for the router's store. The database `CentralRoutingTopology2Router` is created on first run; the connection string is in `Router/appsettings.json`.
- Open `Topology2.sln` in this folder. It contains the sample, its messages, and the CentralRouting projects including the CLI. Build it and define the CLI function, as in Topology 1:

  ```powershell
  dotnet build Samples\Topology2-SitePerService\Topology2.sln
  . Samples\Topology2-SitePerService\routing.ps1
  ```

The router listens on port 5080, like Topology 1's, so run one of them at a time.

## 1. Start everything

Set the seven `Topology2.*` projects (all but `Topology2.Messages`) as startup projects and press F5. Endpoints wait for the router if it isn't up yet. Or run `Samples\Topology2-SitePerService\run.ps1`: it builds the solution and opens a Windows Terminal tab with a pane per process, a row per virtual host, and a pane with the CLI.

- The router logs one `attached to site … through interface …` line per virtual host.
- Each endpoint logs `Local site: sales` (or `billing`, `shipping`). It matched its connection's identity, such as `rabbitmq:v1:localhost/billing`, against the routing document.
- The router assigns the publishers of `OrderPlaced` (Sales), `OrderBilled` (Billing) and `OrderShipped` (Shipping), which each endpoint declares.

```powershell
routing sites            # three sites, one per virtual host
routing endpoints list   # six endpoints, two per site
routing types list       # Charge and RegisterParcel are commands with no route yet
```

## 2. Route the commands inside each service

```powershell
routing routes set-destination Messages.PlaceOrder Sales
routing routes set-destination Messages.Charge PaymentGateway
routing routes set-destination Messages.RegisterParcel ParcelDelivery
```

Each destination is on the sender's own virtual host, so these messages never touch the router.

## 3. Subscribe across services

```powershell
routing routes subscribe Messages.OrderPlaced Billing
routing routes subscribe Messages.OrderBilled Shipping
routing routes subscribe Messages.OrderShipped Sales
routing document
```

In the document, every site reaches the other two via the `Router` queue on its own virtual host.

Press Enter in Website:

| Window | Output | Hop |
| --- | --- | --- |
| Website | `Sent PlaceOrder …`, `Order … accepted by Sales` | sales, direct |
| Sales | `Order … placed` | sales, direct |
| Billing | `Charging order …` | sales → Router → billing |
| PaymentGateway | `Charged the card for order …` | billing, direct |
| Billing | `Order … billed` | billing, direct (reply) |
| Shipping | `Registering a parcel for order …` | billing → Router → shipping |
| ParcelDelivery | `Registered parcel PD… for order …` | shipping, direct |
| Shipping | `Order … shipped, tracking number PD…` | shipping, direct (reply) |
| Sales | `Order … shipped` | shipping → Router → sales |

## 4. Check the isolation

Open the management UI at http://localhost:15672 (guest/guest) and look at the queues of each virtual host. Each one holds only its own service's queues, plus `Router`, `Router.Control` and `error`. A team with access to the `billing` virtual host can't see or consume the Sales queues; the router is the only thing connected to all three.

## Scenario: a service's endpoint goes offline

1. Close the PaymentGateway window.
2. Press Enter in Website. Billing prints `Charging order …`, and the `Charge` command waits in PaymentGateway's queue on the `billing` virtual host.
3. `routing routes show Messages.Charge`: `Warning`, `Endpoint 'PaymentGateway' has no live instances.`
4. Start PaymentGateway again. The order finishes its way to Shipping and back to Sales.

## Reset

```powershell
sqlcmd -S . -E -Q "DROP DATABASE IF EXISTS CentralRoutingTopology2Router;"
Remove-Item -Recurse -Force "$env:TEMP\CentralRoutingSamples\Topology2"
docker rm -f rabbitmq
```
