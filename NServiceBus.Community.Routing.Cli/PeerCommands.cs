using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Router;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NServiceBus.Community.Routing.Cli
{
    public class PeerSettings : RouterSettings
    {
        [CommandArgument(0, "<peer>")]
        [Description("Peer router name")]
        public string Peer { get; set; }
    }

    public class RequestSubscriptionSettings : MessageTypeSettings
    {
        [CommandOption("-w|--wait")]
        [Description("Wait until the publisher's router grants the request")]
        public bool Wait { get; set; }

        [CommandOption("--timeout <SECONDS>")]
        [Description("How long to wait for the grant. Defaults to 120 seconds")]
        [DefaultValue(120)]
        public int Timeout { get; set; }
    }

    public class ListPeersCommand : RouterCommand<RouterSettings>
    {
        protected override async Task Execute(RouterClient router, RouterSettings settings, CancellationToken cancellationToken)
        {
            var peers = await router.Get<List<PeerView>>("peers", cancellationToken).ConfigureAwait(false);
            Output.Write(settings, peers, _ =>
            {
                if (peers.Count == 0)
                {
                    Output.Empty("No peers are configured.");
                    return;
                }

                var table = Output.Table("Peer", "Channel", "Status", "Revision", "Last snapshot", "Sites", "Endpoints", "Published events");
                foreach (var peer in peers.OrderBy(p => p.Name))
                {
                    table.AddRow($"[aqua]{Markup.Escape(peer.Name)}[/]", peer.Channel.ToString(), Output.Status(peer.Status), peer.Revision.ToString(),
                        Output.Ago(peer.LastReceivedAt), Output.List(peer.Sites), peer.Endpoints.Count.ToString(), peer.PublishedEvents.Count.ToString());
                }
                AnsiConsole.Write(table);
            });
        }
    }

    /// <summary>
    /// What a peer exported in its last snapshot: sites, endpoints and the events it publishes.
    /// </summary>
    public class ShowPeerCommand : RouterCommand<PeerSettings>
    {
        protected override async Task Execute(RouterClient router, PeerSettings settings, CancellationToken cancellationToken)
        {
            var peers = await router.Get<List<PeerView>>("peers", cancellationToken).ConfigureAwait(false);
            var peer = peers.FirstOrDefault(p => string.Equals(p.Name, settings.Peer, StringComparison.OrdinalIgnoreCase))
                       ?? throw new CommandException($"{settings.Peer} is not a configured peer. Configured: {string.Join(", ", peers.Select(p => p.Name))}.");

            Output.Write(settings, peer, p =>
            {
                var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(4)).AddColumn();
                grid.AddRow("[bold]Channel[/]", p.Channel.ToString());
                grid.AddRow("[bold]Status[/]", Output.Status(p.Status));
                grid.AddRow("[bold]Revision[/]", p.Revision.ToString());
                grid.AddRow("[bold]Last snapshot[/]", Output.Time(p.LastReceivedAt));
                grid.AddRow("[bold]Sites[/]", Output.List(p.Sites));
                grid.AddRow("[bold]Endpoints[/]", Output.List(p.Endpoints));
                AnsiConsole.Write(new Panel(grid).Header($"[bold]{Markup.Escape(p.Name)}[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));

                if (p.PublishedEvents.Count > 0)
                {
                    var table = Output.Table("Published event", "Publisher");
                    foreach (var published in p.PublishedEvents.OrderBy(e => e.MessageType))
                    {
                        table.AddRow(Markup.Escape(published.MessageType), Output.Text(published.Publisher));
                    }
                    AnsiConsole.Write(table);
                }
            });
        }
    }

    public class ListSubscriptionRequestsCommand : RouterCommand<RouterSettings>
    {
        protected override async Task Execute(RouterClient router, RouterSettings settings, CancellationToken cancellationToken)
        {
            var requests = await router.Get<List<SubscriptionRequestView>>("subscription-requests", cancellationToken).ConfigureAwait(false);
            Output.Write(settings, requests, _ =>
            {
                if (requests.Count == 0)
                {
                    Output.Empty("No subscription requests.");
                    return;
                }

                var table = Output.Table("Direction", "Message type", "Peer", "Status", "Requested", "Granted");
                foreach (var request in requests)
                {
                    var direction = request.Direction == SubscriptionDirection.Outgoing ? "[blue]→ outgoing[/]" : "[purple]← incoming[/]";
                    table.AddRow(direction, Markup.Escape(request.MessageType), Markup.Escape(request.Peer), Output.Status(request.Status),
                        Output.Ago(request.RequestedAt), Output.Ago(request.GrantedAt));
                }
                AnsiConsole.Write(table);
            });
        }
    }

    /// <summary>
    /// Asks the router that owns the publisher of an event to forward it here. Local endpoints can subscribe once it is granted.
    /// </summary>
    public class RequestSubscriptionCommand : RouterCommand<RequestSubscriptionSettings>
    {
        protected override async Task Execute(RouterClient router, RequestSubscriptionSettings settings, CancellationToken cancellationToken)
        {
            var request = await router.Post<SubscriptionRequestView>("subscription-requests", new CreateSubscriptionRequest(settings.MessageType), cancellationToken)
                .ConfigureAwait(false);

            if (settings.Wait && request.Status != SubscriptionRequestStatus.Granted)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.Timeout));

                request = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync($"Waiting for {Markup.Escape(request.Peer)} to grant the request...", async status =>
                {
                    try
                    {
                        while (true)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
                            var requests = await router.Get<List<SubscriptionRequestView>>("subscription-requests", timeout.Token).ConfigureAwait(false);
                            var current = requests.FirstOrDefault(r => r.Direction == SubscriptionDirection.Outgoing && r.MessageType == settings.MessageType);
                            if (current == null || current.Status == SubscriptionRequestStatus.Granted)
                            {
                                return current;
                            }
                            status.Status($"Waiting for {Markup.Escape(current.Peer)} to grant the request ({current.Status})...");
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }
                }).ConfigureAwait(false);

                if (request == null)
                {
                    throw new CommandException($"The request was not granted within {settings.Timeout} seconds. Check that the peer is online with 'peers list'.");
                }
            }

            Output.Write(settings, request, r =>
            {
                if (r.Status == SubscriptionRequestStatus.Granted)
                {
                    Output.Success($"{r.Peer} granted {r.MessageType}. Add subscribers with 'routes subscribe {r.MessageType} <endpoint>'.");
                }
                else
                {
                    AnsiConsole.MarkupLineInterpolated($"Requested {r.MessageType} from {r.Peer}: [blue]{r.Status}[/]. Run 'subscriptions list' to follow it, or pass --wait.");
                }
            });
        }
    }

    public class WithdrawSubscriptionRequestCommand : RouterCommand<MessageTypeSettings>
    {
        protected override async Task Execute(RouterClient router, MessageTypeSettings settings, CancellationToken cancellationToken)
        {
            await router.Delete(RouterClient.Path("subscription-requests", settings.MessageType), cancellationToken).ConfigureAwait(false);
            if (!settings.Json)
            {
                Output.Success($"Subscription request for {settings.MessageType} withdrawn.");
            }
        }
    }
}
