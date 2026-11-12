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
    public class MessageTypeSettings : RouterSettings
    {
        [CommandArgument(0, "<message-type>")]
        [Description("Full name of the message type, e.g. Messages.PlaceOrder")]
        public string MessageType { get; set; }
    }

    public class ListMessageTypesSettings : RouterSettings
    {
        [CommandOption("-u|--unrouted")]
        [Description("Only types without a route")]
        public bool Unrouted { get; set; }
    }

    public class RouteEndpointSettings : MessageTypeSettings
    {
        [CommandArgument(1, "<endpoint>")]
        [Description("Endpoint name")]
        public string Endpoint { get; set; }
    }

    public class ChangeRouteSettings : RouteEndpointSettings
    {
        [CommandOption("-c|--comment <TEXT>")]
        [Description("Why the route changes, stored with the route")]
        public string Comment { get; set; }
    }

    public class DeleteRouteSettings : MessageTypeSettings
    {
        [CommandOption("-y|--yes")]
        [Description("Do not ask for confirmation")]
        public bool Yes { get; set; }
    }

    public class ValidateRouteSettings : MessageTypeSettings
    {
        [CommandOption("-d|--destination <ENDPOINT>")]
        [Description("Destination of a command route")]
        public string Destination { get; set; }

        [CommandOption("-p|--publisher <ENDPOINT>")]
        [Description("Publisher of an event route")]
        public string Publisher { get; set; }

        [CommandOption("-s|--subscriber <ENDPOINT>")]
        [Description("Subscriber of an event route; repeat for several")]
        public string[] Subscribers { get; set; }

        public override ValidationResult Validate()
        {
            var isEvent = Publisher != null || Subscribers?.Length > 0;
            if (Destination != null && isEvent)
            {
                return ValidationResult.Error("A route has either a destination (command) or a publisher and subscribers (event).");
            }
            return Destination == null && !isEvent
                ? ValidationResult.Error("Specify --destination for a command, or --publisher and --subscriber for an event.")
                : base.Validate();
        }
    }

    public class ListMessageTypesCommand : RouterCommand<ListMessageTypesSettings>
    {
        protected override async Task Execute(RouterClient router, ListMessageTypesSettings settings, CancellationToken cancellationToken)
        {
            var types = await router.Get<List<MessageTypeView>>("message-types", cancellationToken).ConfigureAwait(false);
            if (settings.Unrouted)
            {
                types = types.Where(t => t.RouteKind == null).ToList();
            }

            Output.Write(settings, types, _ =>
            {
                if (types.Count == 0)
                {
                    Output.Empty("No message types.");
                    return;
                }

                var table = Output.Table("Message type", "Kind", "Route", "Status", "Declared by");
                foreach (var type in types.OrderBy(t => t.MessageType))
                {
                    table.AddRow(Markup.Escape(type.MessageType), type.Kind?.ToString() ?? Output.None,
                        type.RouteKind?.ToString() ?? "[yellow]unrouted[/]", Output.Status(type.RouteStatus), Output.List(type.DeclaredPublishers));
                }
                AnsiConsole.Write(table);
            });
        }
    }

    /// <summary>
    /// Endpoints that can handle a message type, with their capability on each site.
    /// </summary>
    public class CandidatesCommand : RouterCommand<MessageTypeSettings>
    {
        protected override async Task Execute(RouterClient router, MessageTypeSettings settings, CancellationToken cancellationToken)
        {
            var candidates = await router.Get<List<CandidateView>>(RouterClient.Path("message-types", settings.MessageType, "candidates"), cancellationToken)
                .ConfigureAwait(false);
            Output.Write(settings, candidates, _ =>
            {
                if (candidates.Count == 0)
                {
                    Output.Empty($"No endpoint handles {settings.MessageType}.");
                    return;
                }

                var table = Output.Table("Endpoint", "Sites");
                foreach (var candidate in candidates)
                {
                    table.AddRow($"[aqua]{Markup.Escape(candidate.Endpoint)}[/]",
                        string.Join("\n", candidate.Sites.OrderBy(s => s.Key).Select(s => $"{Markup.Escape(s.Key)}: {Output.Status(s.Value)}")));
                }
                AnsiConsole.Write(table);
            });
        }
    }

    public class ListRoutesCommand : RouterCommand<RouterSettings>
    {
        protected override async Task Execute(RouterClient router, RouterSettings settings, CancellationToken cancellationToken)
        {
            var routes = await router.Get<List<RouteView>>("routes", cancellationToken).ConfigureAwait(false);
            Output.Write(settings, routes, _ =>
            {
                if (routes.Count == 0)
                {
                    Output.Empty("No routes. Assign one with 'routes set-destination' or 'routes set-publisher'.");
                    return;
                }

                var table = Output.Table("Message type", "Kind", "Destination / publisher", "Subscribers", "Status", "Changed");
                foreach (var route in routes)
                {
                    var target = route.Kind == RouteKind.Command ? Output.Text(route.Destination)
                        : route.PreviousPublisher != null ? $"{Markup.Escape(route.PreviousPublisher)} [yellow]→[/] {Markup.Escape(route.Publisher)}"
                        : Output.Text(route.Publisher);
                    table.AddRow(Markup.Escape(route.MessageType), route.Kind.ToString(), target,
                        route.Kind == RouteKind.Command ? "" : Output.List(route.Subscribers), Output.Status(route.Validation.Status),
                        $"{Output.Text(route.ChangedBy)}, {Output.Ago(route.ChangedAt)}");
                }
                AnsiConsole.Write(table);
            });
        }
    }

    public class ShowRouteCommand : RouterCommand<MessageTypeSettings>
    {
        protected override async Task Execute(RouterClient router, MessageTypeSettings settings, CancellationToken cancellationToken)
        {
            var route = await router.Get<RouteView>(RouterClient.Path("routes", settings.MessageType), cancellationToken).ConfigureAwait(false);
            Output.Write(settings, route, Render);
        }

        public static void Render(RouteView route)
        {
            var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(4)).AddColumn();
            grid.AddRow("[bold]Kind[/]", route.Kind.ToString());
            if (route.Kind == RouteKind.Command)
            {
                grid.AddRow("[bold]Destination[/]", Output.Text(route.Destination));
            }
            else
            {
                grid.AddRow("[bold]Publisher[/]", Output.Text(route.Publisher));
                if (route.PreviousPublisher != null)
                {
                    grid.AddRow("[bold]Migrating from[/]", $"[yellow]{Markup.Escape(route.PreviousPublisher)}[/] [grey]since[/] {Output.Time(route.MigrationStartedAt)}");
                }
                grid.AddRow("[bold]Subscribers[/]", Output.List(route.Subscribers));
            }
            grid.AddRow("[bold]Changed[/]", $"{Output.Text(route.ChangedBy)}, {Output.Time(route.ChangedAt)}");
            if (!string.IsNullOrEmpty(route.Comment))
            {
                grid.AddRow("[bold]Comment[/]", Markup.Escape(route.Comment));
            }
            grid.AddRow("[bold]Status[/]", Output.Status(route.Validation.Status));

            AnsiConsole.Write(new Panel(grid).Header($"[bold]{Markup.Escape(route.MessageType)}[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
            if (route.Validation.Findings.Count > 0)
            {
                AnsiConsole.Write(Output.Findings(route.Validation));
            }
        }
    }

    public class SetDestinationCommand : RouterCommand<ChangeRouteSettings>
    {
        protected override async Task Execute(RouterClient router, ChangeRouteSettings settings, CancellationToken cancellationToken)
        {
            var route = await router.Put<RouteView>(RouterClient.Path("routes", settings.MessageType, "destination"),
                new SetEndpointRequest(settings.Endpoint, settings.Comment), cancellationToken).ConfigureAwait(false);
            Output.Write(settings, route, ShowRouteCommand.Render);
        }
    }

    public class SetPublisherCommand : RouterCommand<ChangeRouteSettings>
    {
        protected override async Task Execute(RouterClient router, ChangeRouteSettings settings, CancellationToken cancellationToken)
        {
            var route = await router.Put<RouteView>(RouterClient.Path("routes", settings.MessageType, "publisher"),
                new SetEndpointRequest(settings.Endpoint, settings.Comment), cancellationToken).ConfigureAwait(false);
            Output.Write(settings, route, ShowRouteCommand.Render);
        }
    }

    /// <summary>
    /// Makes another endpoint the publisher while the current one may still publish.
    /// </summary>
    public class MigratePublisherCommand : RouterCommand<ChangeRouteSettings>
    {
        protected override async Task Execute(RouterClient router, ChangeRouteSettings settings, CancellationToken cancellationToken)
        {
            var route = await router.Post<RouteView>(RouterClient.Path("routes", settings.MessageType, "publisher", "migration"),
                new SetEndpointRequest(settings.Endpoint, settings.Comment), cancellationToken).ConfigureAwait(false);
            Output.Write(settings, route, ShowRouteCommand.Render);
        }
    }

    public class CompleteMigrationCommand : RouterCommand<MessageTypeSettings>
    {
        protected override async Task Execute(RouterClient router, MessageTypeSettings settings, CancellationToken cancellationToken)
        {
            var route = await router.Delete<RouteView>(RouterClient.Path("routes", settings.MessageType, "publisher", "migration"), cancellationToken)
                .ConfigureAwait(false);
            Output.Write(settings, route, ShowRouteCommand.Render);
        }
    }

    public class RollBackMigrationCommand : RouterCommand<MessageTypeSettings>
    {
        protected override async Task Execute(RouterClient router, MessageTypeSettings settings, CancellationToken cancellationToken)
        {
            var route = await router.Post<RouteView>(RouterClient.Path("routes", settings.MessageType, "publisher", "migration", "rollback"), null, cancellationToken)
                .ConfigureAwait(false);
            Output.Write(settings, route, ShowRouteCommand.Render);
        }
    }

    public class SubscribeCommand : RouterCommand<ChangeRouteSettings>
    {
        protected override async Task Execute(RouterClient router, ChangeRouteSettings settings, CancellationToken cancellationToken)
        {
            var route = await router.Post<RouteView>(RouterClient.Path("routes", settings.MessageType, "subscribers"),
                new SetEndpointRequest(settings.Endpoint, settings.Comment), cancellationToken).ConfigureAwait(false);
            Output.Write(settings, route, ShowRouteCommand.Render);
        }
    }

    public class UnsubscribeCommand : RouterCommand<RouteEndpointSettings>
    {
        protected override async Task Execute(RouterClient router, RouteEndpointSettings settings, CancellationToken cancellationToken)
        {
            var route = await router.Delete<RouteView>(RouterClient.Path("routes", settings.MessageType, "subscribers", settings.Endpoint), cancellationToken)
                .ConfigureAwait(false);
            Output.Write(settings, route, ShowRouteCommand.Render);
        }
    }

    public class DeleteRouteCommand : RouterCommand<DeleteRouteSettings>
    {
        protected override async Task Execute(RouterClient router, DeleteRouteSettings settings, CancellationToken cancellationToken)
        {
            if (!settings.Yes)
            {
                if (!AnsiConsole.Profile.Capabilities.Interactive)
                {
                    throw new CommandException("Pass --yes to delete a route without a prompt.");
                }
                if (!await AnsiConsole.ConfirmAsync($"Delete the route of [bold]{Markup.Escape(settings.MessageType)}[/]?", false, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }

            await router.Delete(RouterClient.Path("routes", settings.MessageType), cancellationToken).ConfigureAwait(false);
            if (!settings.Json)
            {
                Output.Success($"Route of {settings.MessageType} deleted.");
            }
        }
    }

    /// <summary>
    /// Validates a proposed route without saving it.
    /// </summary>
    public class ValidateRouteCommand : RouterCommand<ValidateRouteSettings>
    {
        protected override async Task Execute(RouterClient router, ValidateRouteSettings settings, CancellationToken cancellationToken)
        {
            var proposed = new ProposedRoute(settings.MessageType, settings.Destination != null ? RouteKind.Command : RouteKind.Event,
                settings.Destination, settings.Publisher, settings.Subscribers?.ToList() ?? new List<string>());
            var validation = await router.Post<RouteValidation>("routes/validate", proposed, cancellationToken).ConfigureAwait(false);
            Output.Write(settings, validation, v => AnsiConsole.Write(Output.Findings(v)));
        }
    }
}
