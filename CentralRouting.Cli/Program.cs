using System;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console.Cli;

namespace CentralRouting.Cli
{
    static class Program
    {
        static async Task<int> Main(string[] args)
        {
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancellation.Cancel();
            };

            var app = new CommandApp();
            app.Configure(config =>
            {
                config.SetApplicationName("routing");

                config.AddCommand<StatusCommand>("status").WithDescription("Summary of the router: sites, endpoints, routes, peers and requests");
                config.AddCommand<SitesCommand>("sites").WithDescription("Sites the router is attached to");
                config.AddCommand<DocumentCommand>("document").WithDescription("The routing document the router publishes to its endpoints");

                config.AddBranch("endpoints", endpoints =>
                {
                    endpoints.SetDescription("Endpoints that report to the router or were learned from peers");
                    endpoints.AddCommand<ListEndpointsCommand>("list").WithDescription("List endpoints");
                    endpoints.AddCommand<ShowEndpointCommand>("show").WithDescription("Sites, queues, capabilities and instances of an endpoint")
                        .WithExample("endpoints", "show", "Billing");
                    endpoints.AddCommand<SetAddressPolicyCommand>("set-policy").WithDescription("Set the address policy of an endpoint")
                        .WithExample("endpoints", "set-policy", "Billing", "Distribute");
                    endpoints.AddCommand<DisableInstanceCommand>("disable-instance").WithDescription("Stop sending to an instance's queue");
                    endpoints.AddCommand<EnableInstanceCommand>("enable-instance").WithDescription("Send to an instance's queue again");
                    endpoints.AddCommand<RemoveInstanceCommand>("remove-instance").WithDescription("Forget an instance that is gone for good");
                    endpoints.AddCommand<DisableSiteCommand>("disable-site").WithDescription("Stop sending to an endpoint on a site");
                    endpoints.AddCommand<EnableSiteCommand>("enable-site").WithDescription("Send to an endpoint on a site again");
                });

                config.AddBranch("types", types =>
                {
                    types.SetDescription("Message types the router knows about");
                    types.AddCommand<ListMessageTypesCommand>("list").WithDescription("List message types and whether they are routed");
                    types.AddCommand<CandidatesCommand>("candidates").WithDescription("Endpoints that can handle a message type")
                        .WithExample("types", "candidates", "Messages.PlaceOrder");
                });

                config.AddBranch("routes", routes =>
                {
                    routes.SetDescription("Command and event routes");
                    routes.AddCommand<ListRoutesCommand>("list").WithDescription("List routes with their validation status");
                    routes.AddCommand<ShowRouteCommand>("show").WithDescription("A route with its validation findings");
                    routes.AddCommand<SetDestinationCommand>("set-destination").WithDescription("Route a command to an endpoint")
                        .WithExample("routes", "set-destination", "Messages.PlaceOrder", "Billing", "--comment", "\"Billing owns orders\"");
                    routes.AddCommand<SetPublisherCommand>("set-publisher").WithDescription("Make an endpoint the single publisher of an event")
                        .WithExample("routes", "set-publisher", "Messages.OrderPlaced", "Billing");
                    routes.AddCommand<MigratePublisherCommand>("migrate-publisher").WithDescription("Move an event to a new publisher; both may publish until the migration completes")
                        .WithExample("routes", "migrate-publisher", "Messages.OrderPlaced", "BillingV2");
                    routes.AddCommand<CompleteMigrationCommand>("complete-migration").WithDescription("Only the new publisher may publish from now on");
                    routes.AddCommand<RollBackMigrationCommand>("rollback-migration").WithDescription("Make the previous publisher the only publisher again");
                    routes.AddCommand<SubscribeCommand>("subscribe").WithDescription("Subscribe an endpoint to an event")
                        .WithExample("routes", "subscribe", "Messages.OrderPlaced", "Sales");
                    routes.AddCommand<UnsubscribeCommand>("unsubscribe").WithDescription("Remove a subscriber of an event");
                    routes.AddCommand<DeleteRouteCommand>("delete").WithDescription("Delete a route");
                    routes.AddCommand<ValidateRouteCommand>("validate").WithDescription("Validate a route without saving it")
                        .WithExample("routes", "validate", "Messages.PlaceOrder", "--destination", "Billing");
                });

                config.AddBranch("peers", peers =>
                {
                    peers.SetDescription("Peer routers");
                    peers.AddCommand<ListPeersCommand>("list").WithDescription("Configured peers and the state of their snapshots");
                    peers.AddCommand<ShowPeerCommand>("show").WithDescription("What a peer exported: sites, endpoints and published events");
                });

                config.AddBranch("subscriptions", subscriptions =>
                {
                    subscriptions.SetDescription("Subscription requests to and from peer routers");
                    subscriptions.AddCommand<ListSubscriptionRequestsCommand>("list").WithDescription("Outgoing and incoming requests");
                    subscriptions.AddCommand<RequestSubscriptionCommand>("request").WithDescription("Ask the publisher's router to forward an event here")
                        .WithExample("subscriptions", "request", "Messages.PriceListChanged", "--wait");
                    subscriptions.AddCommand<WithdrawSubscriptionRequestCommand>("withdraw").WithDescription("Withdraw a request once no endpoint subscribes");
                });
            });

            return await app.RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
    }
}
