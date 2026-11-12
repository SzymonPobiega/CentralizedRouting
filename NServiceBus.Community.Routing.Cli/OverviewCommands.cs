using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Model;
using NServiceBus.Community.Routing.Router;
using Spectre.Console;

namespace NServiceBus.Community.Routing.Cli
{
    /// <summary>
    /// A summary of the router: its sites, endpoints and instances, routes by validation status, peers and subscription requests.
    /// </summary>
    public class StatusCommand : RouterCommand<RouterSettings>
    {
        protected override async Task Execute(RouterClient router, RouterSettings settings, CancellationToken cancellationToken)
        {
            var document = router.Get<RoutingDocument>("routing-document", cancellationToken);
            var endpoints = router.Get<List<EndpointView>>("endpoints", cancellationToken);
            var routes = router.Get<List<RouteView>>("routes", cancellationToken);
            var peers = router.Get<List<PeerView>>("peers", cancellationToken);
            var requests = router.Get<List<SubscriptionRequestView>>("subscription-requests", cancellationToken);
            await Task.WhenAll(document, endpoints, routes, peers, requests).ConfigureAwait(false);

            var status = new
            {
                Router = document.Result.Router,
                document.Result.Revision,
                Sites = document.Result.Sites.Keys.OrderBy(s => s).ToList(),
                Endpoints = endpoints.Result.Count,
                Instances = endpoints.Result.SelectMany(e => e.Instances).GroupBy(i => i.Status).ToDictionary(g => g.Key, g => g.Count()),
                Routes = routes.Result.GroupBy(r => r.Validation.Status).ToDictionary(g => g.Key, g => g.Count()),
                Peers = peers.Result.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count()),
                Requests = requests.Result.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count())
            };

            Output.Write(settings, status, s =>
            {
                var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(4)).AddColumn();
                grid.AddRow("[bold]Router[/]", $"{Markup.Escape(s.Router)} [grey]at {Markup.Escape(router.Url.ToString())}[/]");
                grid.AddRow("[bold]Revision[/]", s.Revision.ToString());
                grid.AddRow("[bold]Sites[/]", string.Join(", ", s.Sites.Select(Markup.Escape)));
                grid.AddRow("[bold]Endpoints[/]", $"{s.Endpoints} [grey](instances: {Counts(s.Instances, i => Output.Status(i, false))})[/]");
                grid.AddRow("[bold]Routes[/]", Counts(s.Routes, r => Output.Status(r)));
                grid.AddRow("[bold]Peers[/]", Counts(s.Peers, Output.Status));
                grid.AddRow("[bold]Subscription requests[/]", Counts(s.Requests, Output.Status));

                AnsiConsole.Write(new Panel(grid).Header("[bold]Central routing[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));

                var invalid = routes.Result.Where(r => r.Validation.Status != ValidationStatus.Valid).ToList();
                if (invalid.Count > 0)
                {
                    var table = Output.Table("Message type", "Status", "Findings");
                    foreach (var route in invalid)
                    {
                        table.AddRow(Markup.Escape(route.MessageType), Output.Status(route.Validation.Status),
                            Output.List(route.Validation.Findings.Select(f => f.Message)));
                    }
                    AnsiConsole.Write(table);
                }
            });
        }

        static string Counts<T>(Dictionary<T, int> counts, System.Func<T, string> label)
        {
            return counts.Count == 0 ? Output.None : string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Value} {label(c.Key)}"));
        }
    }

    public class SitesCommand : RouterCommand<RouterSettings>
    {
        protected override async Task Execute(RouterClient router, RouterSettings settings, CancellationToken cancellationToken)
        {
            var sites = await router.Get<List<SiteView>>("sites", cancellationToken).ConfigureAwait(false);
            Output.Write(settings, sites, _ =>
            {
                var table = Output.Table("Site", "Interface", "Status", "Router address", "Identities");
                foreach (var site in sites.OrderBy(s => s.SiteId))
                {
                    var status = site.Status switch
                    {
                        SiteStatus.Connected => "[green]Connected[/]",
                        SiteStatus.Unreachable => "[red]Unreachable[/]",
                        _ => "[yellow]Connecting[/]"
                    };
                    table.AddRow(Markup.Escape(site.SiteId), Output.Text(site.Interface), status, Output.Text(site.RouterAddress), Output.List(site.Identities));
                }
                AnsiConsole.Write(table);
            });
        }
    }

    /// <summary>
    /// The routing document the router publishes to its endpoints, as a tree.
    /// </summary>
    public class DocumentCommand : RouterCommand<RouterSettings>
    {
        protected override async Task Execute(RouterClient router, RouterSettings settings, CancellationToken cancellationToken)
        {
            var document = await router.Get<RoutingDocument>("routing-document", cancellationToken).ConfigureAwait(false);
            Output.Write(settings, document, d =>
            {
                var tree = new Tree($"[bold]{Markup.Escape(d.Router)}[/] revision {d.Revision} [grey]generated {Output.Time(d.GeneratedAt)}[/]");

                var sites = tree.AddNode("[bold]Sites[/]");
                foreach (var (siteId, site) in d.Sites.OrderBy(s => s.Key))
                {
                    var node = sites.AddNode($"[aqua]{Markup.Escape(siteId)}[/] [grey]router queue[/] {Output.Text(site.Router)}");
                    foreach (var identity in site.Identities)
                    {
                        node.AddNode($"[grey]identity[/] {Markup.Escape(identity)}");
                    }
                    foreach (var (destination, hop) in site.NextHop.OrderBy(h => h.Key))
                    {
                        node.AddNode($"[grey]to[/] {Markup.Escape(destination)} [grey]via[/] {Markup.Escape(hop)}");
                    }
                }

                var endpoints = tree.AddNode("[bold]Endpoints[/]");
                foreach (var (name, endpoint) in d.Endpoints.OrderBy(e => e.Key))
                {
                    var node = endpoints.AddNode($"[aqua]{Markup.Escape(name)}[/] [grey]{endpoint.Policy}[/]");
                    foreach (var (siteId, queues) in endpoint.Addresses.OrderBy(a => a.Key))
                    {
                        node.AddNode($"{Markup.Escape(siteId)}: {Output.Queues(queues).Replace("\n", ", ")}");
                    }
                }

                var commands = tree.AddNode("[bold]Commands[/]");
                foreach (var (type, route) in d.Routes.OrderBy(r => r.Key))
                {
                    commands.AddNode($"{Markup.Escape(type)} [grey]→[/] [aqua]{Markup.Escape(route.Endpoint)}[/]");
                }

                var events = tree.AddNode("[bold]Events[/]");
                foreach (var (type, route) in d.Events.OrderBy(e => e.Key))
                {
                    var forward = route.Forward ? " [yellow](forwarded to peers)[/]" : "";
                    var node = events.AddNode($"{Markup.Escape(type)} [grey]published by[/] [aqua]{Output.Text(route.Publisher)}[/]{forward}");
                    foreach (var subscriber in route.Subscribers)
                    {
                        node.AddNode($"[grey]→[/] {Markup.Escape(subscriber)}");
                    }
                }

                AnsiConsole.Write(tree);
            });
        }
    }
}
