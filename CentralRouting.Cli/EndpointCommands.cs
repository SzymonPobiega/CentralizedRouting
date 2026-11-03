using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralRouting.Model;
using CentralRouting.Router;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CentralRouting.Cli
{
    public class EndpointSettings : RouterSettings
    {
        [CommandArgument(0, "<endpoint>")]
        [Description("Endpoint name")]
        public string Endpoint { get; set; }
    }

    public class SetAddressPolicySettings : EndpointSettings
    {
        [CommandArgument(1, "<policy>")]
        [Description("Strict, Distribute, PreferLocal, Explicit or NewestSite")]
        public AddressPolicy Policy { get; set; }
    }

    public class InstanceSettings : EndpointSettings
    {
        [CommandArgument(1, "<instance>")]
        [Description("Instance id")]
        public string Instance { get; set; }
    }

    public class EndpointSiteSettings : EndpointSettings
    {
        [CommandArgument(1, "<site>")]
        [Description("Site id")]
        public string Site { get; set; }
    }

    public class ListEndpointsCommand : RouterCommand<RouterSettings>
    {
        protected override async Task Execute(RouterClient router, RouterSettings settings, CancellationToken cancellationToken)
        {
            var endpoints = await router.Get<List<EndpointView>>("endpoints", cancellationToken).ConfigureAwait(false);
            Output.Write(settings, endpoints, _ =>
            {
                if (endpoints.Count == 0)
                {
                    Output.Empty("No endpoint has reported to the router or been learned from a peer yet.");
                    return;
                }

                var table = Output.Table("Endpoint", "Owner", "Policy", "Sites", "Instances");
                foreach (var endpoint in endpoints.OrderBy(e => e.Name))
                {
                    var sites = endpoint.Sites.Select(s => s.Disabled ? $"{Markup.Escape(s.SiteId)} [red](disabled)[/]" : Markup.Escape(s.SiteId));
                    var instances = endpoint.Instances.Count == 0
                        ? "[grey]learned[/]"
                        : string.Join(", ", endpoint.Instances.GroupBy(i => i.Status).OrderBy(g => g.Key).Select(g => $"{g.Count()} {Output.Status(g.Key, false)}"));
                    table.AddRow($"[aqua]{Markup.Escape(endpoint.Name)}[/]", Output.Text(endpoint.Owner), endpoint.AddressPolicy.ToString(),
                        string.Join("\n", sites), instances);
                }
                AnsiConsole.Write(table);
            });
        }
    }

    /// <summary>
    /// An endpoint's sites with their queues, the capability of each site for each message type, and its instances.
    /// </summary>
    public class ShowEndpointCommand : RouterCommand<EndpointSettings>
    {
        protected override async Task Execute(RouterClient router, EndpointSettings settings, CancellationToken cancellationToken)
        {
            var endpoint = await router.Get<EndpointView>(RouterClient.Path("endpoints", settings.Endpoint), cancellationToken).ConfigureAwait(false);
            Output.Write(settings, endpoint, Render);
        }

        public static void Render(EndpointView endpoint)
        {
            Output.Heading($"{endpoint.Name} (owner {endpoint.Owner}, policy {endpoint.AddressPolicy})");

            var sites = Output.Table("Site", "First seen", "Disabled", "Queues");
            foreach (var site in endpoint.Sites)
            {
                sites.AddRow(Markup.Escape(site.SiteId), Output.Time(site.FirstSeen), site.Disabled ? "[red]yes[/]" : "no", Output.Queues(site.Addresses));
            }
            AnsiConsole.Write(sites);

            var types = endpoint.Sites.SelectMany(s => s.Capabilities.Keys).Distinct().OrderBy(t => t).ToList();
            if (types.Count > 0)
            {
                var capabilities = Output.Table(new[] { "Message type" }.Concat(endpoint.Sites.Select(s => s.SiteId)).ToArray());
                foreach (var type in types)
                {
                    capabilities.AddRow(new[] { Markup.Escape(type) }
                        .Concat(endpoint.Sites.Select(s => s.Capabilities.TryGetValue(type, out var state) ? Output.Status(state) : Output.None))
                        .ToArray());
                }
                AnsiConsole.Write(capabilities);
            }

            if (endpoint.Instances.Count > 0)
            {
                var instances = Output.Table("Instance", "Site", "Queue", "Host", "Status", "Last report", "Revision");
                foreach (var instance in endpoint.Instances)
                {
                    instances.AddRow(Markup.Escape(instance.InstanceId), Output.Text(instance.SiteId), Output.Text(instance.InputQueue),
                        Output.Text(instance.HostName), Output.Status(instance.Status, instance.Disabled), Output.Ago(instance.LastReportAt),
                        instance.DocumentRevision.ToString());
                }
                AnsiConsole.Write(instances);
            }
        }
    }

    public class SetAddressPolicyCommand : RouterCommand<SetAddressPolicySettings>
    {
        protected override async Task Execute(RouterClient router, SetAddressPolicySettings settings, CancellationToken cancellationToken)
        {
            var endpoint = await router.Put<EndpointView>(RouterClient.Path("endpoints", settings.Endpoint, "address-policy"),
                new SetAddressPolicyRequest(settings.Policy), cancellationToken).ConfigureAwait(false);
            Output.Write(settings, endpoint, e => Output.Success($"{e.Name} now uses the {e.AddressPolicy} address policy."));
        }
    }

    public abstract class SetInstanceDisabledCommand : RouterCommand<InstanceSettings>
    {
        readonly bool disabled;

        protected SetInstanceDisabledCommand(bool disabled)
        {
            this.disabled = disabled;
        }

        protected override async Task Execute(RouterClient router, InstanceSettings settings, CancellationToken cancellationToken)
        {
            var endpoint = await router.Put<EndpointView>(RouterClient.Path("endpoints", settings.Endpoint, "instances", settings.Instance, "disabled"),
                new SetDisabledRequest(disabled), cancellationToken).ConfigureAwait(false);
            Output.Write(settings, endpoint, _ => Output.Success($"Instance {settings.Instance} of {settings.Endpoint} {(disabled ? "disabled" : "enabled")}."));
        }
    }

    public class DisableInstanceCommand : SetInstanceDisabledCommand
    {
        public DisableInstanceCommand() : base(true)
        {
        }
    }

    public class EnableInstanceCommand : SetInstanceDisabledCommand
    {
        public EnableInstanceCommand() : base(false)
        {
        }
    }

    public class RemoveInstanceCommand : RouterCommand<InstanceSettings>
    {
        protected override async Task Execute(RouterClient router, InstanceSettings settings, CancellationToken cancellationToken)
        {
            var endpoint = await router.Delete<EndpointView>(RouterClient.Path("endpoints", settings.Endpoint, "instances", settings.Instance), cancellationToken)
                .ConfigureAwait(false);
            Output.Write(settings, endpoint, _ => Output.Success($"Instance {settings.Instance} removed from {settings.Endpoint}. It comes back if it reports again."));
        }
    }

    public abstract class SetSiteDisabledCommand : RouterCommand<EndpointSiteSettings>
    {
        readonly bool disabled;

        protected SetSiteDisabledCommand(bool disabled)
        {
            this.disabled = disabled;
        }

        protected override async Task Execute(RouterClient router, EndpointSiteSettings settings, CancellationToken cancellationToken)
        {
            var endpoint = await router.Put<EndpointView>(RouterClient.Path("endpoints", settings.Endpoint, "sites", settings.Site, "disabled"),
                new SetDisabledRequest(disabled), cancellationToken).ConfigureAwait(false);
            Output.Write(settings, endpoint, _ => Output.Success($"{settings.Endpoint} on site {settings.Site} {(disabled ? "disabled" : "enabled")}."));
        }
    }

    public class DisableSiteCommand : SetSiteDisabledCommand
    {
        public DisableSiteCommand() : base(true)
        {
        }
    }

    public class EnableSiteCommand : SetSiteDisabledCommand
    {
        public EnableSiteCommand() : base(false)
        {
        }
    }
}
