using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CentralRouting.Model;
using CentralRouting.Router;
using Spectre.Console;
using Spectre.Console.Json;
using Spectre.Console.Rendering;

namespace CentralRouting.Cli
{
    /// <summary>
    /// Formatting shared by the commands.
    /// </summary>
    public static class Output
    {
        public const string None = "[grey]-[/]";

        static readonly JsonSerializerOptions IndentedJson = new JsonSerializerOptions(RoutingJson.Options) { WriteIndented = true };

        /// <summary>
        /// Writes the value as JSON when asked to, otherwise runs the formatted rendering.
        /// </summary>
        public static void Write<T>(RouterSettings settings, T value, Action<T> render)
        {
            if (settings.Json)
            {
                //Piped output stays plain so that it is not wrapped at the console width
                if (Console.IsOutputRedirected)
                {
                    Console.WriteLine(JsonSerializer.Serialize(value, IndentedJson));
                }
                else
                {
                    AnsiConsole.Write(new JsonText(RoutingJson.Serialize(value)));
                    AnsiConsole.WriteLine();
                }
                return;
            }

            render(value);
        }

        public static Table Table(params string[] columns)
        {
            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            foreach (var column in columns)
            {
                table.AddColumn(new TableColumn($"[bold]{column}[/]"));
            }
            return table;
        }

        public static void Empty(string message)
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
        }

        public static void Success(string message)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
        }

        public static void Heading(string title)
        {
            AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(title)}[/]").LeftJustified().RuleStyle("grey"));
        }

        public static string Text(string value) => string.IsNullOrEmpty(value) ? None : Markup.Escape(value);

        public static string List(IEnumerable<string> values)
        {
            var list = values?.ToList();
            return list == null || list.Count == 0 ? None : string.Join("\n", list.Select(Markup.Escape));
        }

        public static string Time(DateTimeOffset? value)
        {
            if (value == null || value == default(DateTimeOffset))
            {
                return None;
            }

            return $"{value.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} [grey]({Relative(value.Value)})[/]";
        }

        /// <summary>
        /// Only how long ago, for tables with many columns.
        /// </summary>
        public static string Ago(DateTimeOffset? value)
        {
            return value == null || value == default(DateTimeOffset) ? None : Relative(value.Value);
        }

        static string Relative(DateTimeOffset value)
        {
            var age = DateTimeOffset.UtcNow - value;
            return age.TotalSeconds < 1 ? "just now"
                : age.TotalSeconds < 60 ? $"{age.TotalSeconds:0}s ago"
                : age.TotalMinutes < 60 ? $"{age.TotalMinutes:0}m ago"
                : age.TotalHours < 48 ? $"{age.TotalHours:0}h ago"
                : $"{age.TotalDays:0}d ago";
        }

        public static string Status(ValidationStatus? status) => status switch
        {
            ValidationStatus.Valid => "[green]Valid[/]",
            ValidationStatus.Warning => "[yellow]Warning[/]",
            ValidationStatus.Invalid => "[red]Invalid[/]",
            _ => None
        };

        public static string Status(CapabilityState state) => state switch
        {
            CapabilityState.Capable => "[green]Capable[/]",
            CapabilityState.Partial => "[yellow]Partial[/]",
            CapabilityState.Missing => "[red]Missing[/]",
            _ => "[grey]Offline[/]"
        };

        public static string Status(InstanceStatus status, bool disabled)
        {
            var text = status switch
            {
                InstanceStatus.Live => "[green]Live[/]",
                InstanceStatus.Stale => "[yellow]Stale[/]",
                _ => "[grey]Stopped[/]"
            };
            return disabled ? text + " [red](disabled)[/]" : text;
        }

        public static string Status(PeerStatus status) => status switch
        {
            PeerStatus.Online => "[green]Online[/]",
            PeerStatus.Offline => "[red]Offline[/]",
            _ => "[grey]Unknown[/]"
        };

        public static string Status(SubscriptionRequestStatus status) => status switch
        {
            SubscriptionRequestStatus.Granted => "[green]Granted[/]",
            SubscriptionRequestStatus.Granting => "[yellow]Granting[/]",
            _ => "[blue]Requested[/]"
        };

        /// <summary>
        /// Queues with the number of live instances behind each, e.g. "Billing ×2".
        /// </summary>
        public static string Queues(IReadOnlyDictionary<string, int> queues)
        {
            return queues == null || queues.Count == 0
                ? None
                : string.Join("\n", queues.OrderBy(q => q.Key).Select(q => $"{Markup.Escape(q.Key)} [grey]×{q.Value}[/]"));
        }

        /// <summary>
        /// The findings of a route validation, or nothing when the route is valid.
        /// </summary>
        public static IRenderable Findings(RouteValidation validation)
        {
            if (validation.Findings.Count == 0)
            {
                return new Markup($"{Status(validation.Status)}");
            }

            var table = Table("Severity", "Code", "Finding");
            foreach (var finding in validation.Findings)
            {
                table.AddRow(Status(finding.Severity), Markup.Escape(finding.Code), Markup.Escape(finding.Message));
            }
            return table;
        }
    }
}
