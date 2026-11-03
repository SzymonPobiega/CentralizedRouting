# Topology 5: migration between sites

The Topology 1 system starts entirely on RabbitMQ and moves to Azure Service Bus one endpoint at a time. One router owns both sites for the duration. Every endpoint uses the NewestSite policy: while it runs on both sites, its traffic goes to the site where it appeared last, and the old instance drains its queue.

| Site | Transport | Endpoints at the start | Endpoints at the end |
| --- | --- | --- | --- |
| `rabbit` | RabbitMQ virtual host `topology5` | Website, Sales, Billing, Shipping | none |
| `azure` | Azure Service Bus | none | Website, Sales, Billing, Shipping |

```
Website ──PlaceOrder──▶ Sales ──OrderPlaced──▶ Billing ──OrderBilled──▶ Shipping ──OrderShipped──▶ Sales
```

Every endpoint project runs on either site: `--site rabbit` (the default) or `--site azure`. The launch profiles `rabbit` and `azure` pass that argument. Two copies of one endpoint run from the same folder, so each gets an instance ID with its site in it, such as `MSI-Billing-rabbit`.

## Before you start

- RabbitMQ with the management plugin on localhost, as in Topology 2. The sample creates the virtual host `topology5`.
- `AzureServiceBus_ConnectionString` set to a namespace with Manage rights.
- A local SQL Server default instance for the router's store, `CentralRoutingTopology5Router`.
- The router listens on port 5095. It uses the queues `Router` and `Router.Control`, as Topology 1's router does, so don't run the two samples at the same time.
- Open `Topology5.sln` in this folder. It contains the sample, its messages, and the CentralRouting projects including the CLI. Build it and define the CLI function:

  ```powershell
  dotnet build Samples\Topology5-Migration\Topology5.sln
  . Samples\Topology5-Migration\routing.ps1
  ```

## 1. Start on RabbitMQ

Set the five `Topology5.*` projects (all but `Topology5.Messages`) as startup projects, with the `rabbit` launch profile for the endpoints (the default), and press F5. Each endpoint logs `Local site: rabbit`, and the router assigns the three publishers.

Or run `Samples\Topology5-Migration\run.ps1`. It builds the solution and opens a Windows Terminal tab with a row per endpoint and a column per site, and a pane with the CLI. The panes in the `azure` column wait: press Enter in one to start that endpoint on Azure Service Bus, instead of the `dotnet run` commands in the steps below.

```powershell
routing routes set-destination Messages.PlaceOrder Sales
routing routes subscribe Messages.OrderPlaced Billing
routing routes subscribe Messages.OrderBilled Shipping
routing routes subscribe Messages.OrderShipped Sales
```

Wait a few seconds for the endpoints to reload the document, then press Enter in Website. The order goes all the way round on RabbitMQ. The router doesn't touch it.

Endpoints reload the routing document every 5 seconds. An order placed right after the subscriptions are added can reach Sales before it reloads, and then its `OrderPlaced` has no subscribers yet.

## 2. Prepare the migration

```powershell
foreach ($e in 'Website', 'Sales', 'Billing', 'Shipping') { routing endpoints set-policy $e NewestSite }
```

With one site, NewestSite behaves like Strict, so nothing changes yet.

## 3. Move Billing

Start a second Billing, on Azure, from a terminal. `--no-build` keeps it from rebuilding the project Visual Studio is running.

```powershell
dotnet run --project Samples\Topology5-Migration\Billing --no-build --launch-profile azure
```

It logs `Local site: azure`. The router switches as soon as it reports:

```powershell
routing endpoints show Billing
```

```
Site     First seen                     Queues
azure    2026-11-02 21:51:11 (5s ago)   Billing x1
rabbit   2026-11-02 21:48:55 (2m ago)   -

Message type          azure     rabbit
Messages.OrderPlaced  Capable   Capable

Instance             Site     Status
MSI-Billing-azure    azure    Live
MSI-Billing-rabbit   rabbit   Live
```

Both instances are live, but only `azure` publishes a queue. The switch waits for the readiness gate: the new site needs a live instance that handles every message type routed to Billing. Here it does from the start.

Press Enter in Website:

| Window | Output | Path |
| --- | --- | --- |
| Sales (rabbit) | `Order … placed` | rabbit, direct |
| Billing (azure) | `Order … billed` | rabbit → Router → azure |
| Shipping (rabbit) | `Order … shipped` | azure → Router → rabbit |
| Sales (rabbit) | `Order … shipped` | rabbit, direct |

Billing (rabbit) prints nothing new. It keeps consuming its queue, so messages sent before the switch, including any from senders that hadn't reloaded yet, are still processed.

### Retire the old instance

Once the `Billing` queue in the `topology5` virtual host is empty (in the management UI, http://localhost:15672), close Billing (rabbit) and remove its instance:

```powershell
routing endpoints remove-instance Billing MSI-Billing-rabbit
```

Close the old instance before you remove it. Otherwise it reports again within seconds and comes back. `routing endpoints list` still shows `rabbit` among Billing's sites. The router remembers when Billing first appeared there, so an old instance restarted by mistake doesn't count as the newest site and can't pull the traffic back.

## 4. Move Shipping, and roll it back

Start Shipping on Azure the same way:

```powershell
dotnet run --project Samples\Topology5-Migration\Shipping --no-build --launch-profile azure
```

Suppose something is wrong with the new deployment. Disable the endpoint on the new site:

```powershell
routing endpoints disable-site Shipping azure
```

The next document routes Shipping back to `rabbit`. Press Enter in Website, and Shipping (rabbit) ships the order, while Shipping (azure) keeps running and gets nothing. To go ahead after all:

```powershell
routing endpoints enable-site Shipping azure
```

The next order is shipped by Shipping (azure). Retire Shipping (rabbit) as in step 3:

```powershell
routing endpoints remove-instance Shipping MSI-Shipping-rabbit
```

## 5. Move Sales

```powershell
dotnet run --project Samples\Topology5-Migration\Sales --no-build --launch-profile azure
```

Website, still on RabbitMQ, now sends `PlaceOrder` through the router to Sales on Azure, and the reply comes back the same way. `OrderPlaced`, `OrderBilled` and `OrderShipped` now all stay on Azure. Retire Sales (rabbit):

```powershell
routing endpoints remove-instance Sales MSI-Sales-rabbit
```

## 6. Move Website

```powershell
dotnet run --project Samples\Topology5-Migration\Website --no-build --launch-profile azure
```

Place orders from the new Website window. Close Website (rabbit) once the replies to its own orders have arrived, because they go back to the queue it was listening on. Then remove it:

```powershell
routing endpoints remove-instance Website MSI-Website-rabbit
routing document
```

Every endpoint now has an address only on `azure`, so every message stays on Azure Service Bus and the router forwards nothing. It still serves the routing document, and its `rabbit` interface can be removed once nothing on RabbitMQ needs to reach Azure any more. NewestSite can stay, or you can set the endpoints back to Strict.

## Reset

Stop everything, then:

```powershell
sqlcmd -S . -E -Q "DROP DATABASE IF EXISTS CentralRoutingTopology5Router;"
Remove-Item -Recurse -Force "$env:TEMP\CentralRoutingSamples\Topology5"
docker exec rabbitmq rabbitmqctl delete_vhost topology5
```
