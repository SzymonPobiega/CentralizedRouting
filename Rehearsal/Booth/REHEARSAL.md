# Booth demo rehearsal

The conference booth demo's sites with central routing instead of the messaging bridge, on one machine. The brokers and routers run in Docker; the endpoints, simplified versions of the booth's, run on the host.

| Site | Transport | Endpoints | Router |
| --- | --- | --- | --- |
| `booth` | RabbitMQ (the Gateway Pi) | Sales, Client | Booth router |
| `client` | PostgreSQL (the ClientUI Pi's local database) | Client in robust mode | Booth router |
| `azure` | Azure Service Bus | Billing, Shipping | Cloud router, which peers with the Booth router over this site |

The Client places an order every two seconds. Sales publishes `OrderPlaced` to Billing and Shipping, Billing publishes `OrderBilled` to Shipping and Sales, and Shipping publishes `OrderShipped` to Sales. Billing and Shipping report to the Cloud router because, in Azure, they can't reach the Booth router's API on the Pi.

## Running it

Needs Docker and the `AzureServiceBus_ConnectionString` environment variable. The routers and endpoints create their queues in that namespace, all prefixed with `Booth` or `Cloud`.

```powershell
.\run.ps1                   # the Client in robust mode; -RabbitMQClient runs it on RabbitMQ
.\setup.ps1                 # in the CLI pane, once all endpoints run
booth sites                 # the Booth router's view, including whether each site is reachable
docker compose down         # stop; add -v to drop the databases
```

## Scenarios

**Pull the ClientUI Pi's cable.** The Booth router reaches the client database only through the `clientlan` network:

```powershell
docker network disconnect booth-rehearsal_clientlan booth-rehearsal-booth-router-1
docker network connect --alias client-pi booth-rehearsal_clientlan booth-rehearsal-booth-router-1
```

The Client keeps placing orders. They wait in the router's queue in the client database (`SELECT count(*) FROM "BoothRouter"` in `booth-rehearsal-clientdb-1`) and reach Sales once the cable is back.

**RabbitMQ goes down while the Client is in robust mode.**

```powershell
docker stop booth-rehearsal-rabbitmq-1
docker start booth-rehearsal-rabbitmq-1
```

The router can't forward the orders to Sales, so it moves them to its holding queue for the booth site in the client database (`SELECT count(*) FROM "BoothRouter.Pending.booth"`), and `booth sites` shows the booth site as `Unreachable`. Once RabbitMQ is back, the holding queue drains within seconds.

**The router restarts while the ClientUI Pi is unreachable.** Disconnect the network as above, then `docker restart booth-rehearsal-booth-router-1`. The router starts without the client site (`Connecting`), keeps trying every 5 seconds, and attaches once the network is back.

## Not covered

- Azure Service Bus becoming unreachable from the Booth router. The router takes the same path as for RabbitMQ, but Docker can't cut one container's internet access without also cutting its published API port.
- The Particular platform. On the booth, the client site gets a platform instance of its own instead of forwarding its platform queues.
- The Cloud router keeps its state in memory here; in Azure it needs durable storage.
