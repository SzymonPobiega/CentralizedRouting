using System;
using System.ComponentModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CentralRouting.Cli
{
    /// <summary>
    /// Options every command takes.
    /// </summary>
    public class RouterSettings : CommandSettings
    {
        public const string RouterUrlVariable = "CENTRALROUTING_ROUTER";

        [CommandOption("-r|--router <URL>")]
        [Description("Router base URL. Defaults to the " + RouterUrlVariable + " environment variable, then http://localhost:5080")]
        public string Router { get; set; }

        [CommandOption("--json")]
        [Description("Write the API response as JSON instead of formatted output")]
        public bool Json { get; set; }

        public Uri RouterUrl => new Uri(Router ?? Environment.GetEnvironmentVariable(RouterUrlVariable) ?? "http://localhost:5080");

        public override ValidationResult Validate()
        {
            var url = Router ?? Environment.GetEnvironmentVariable(RouterUrlVariable);
            return url == null || Uri.TryCreate(url, UriKind.Absolute, out _)
                ? ValidationResult.Success()
                : ValidationResult.Error($"'{url}' is not an absolute URL.");
        }
    }

    /// <summary>
    /// Creates the API client and turns API and connection errors into a message and exit code 1.
    /// </summary>
    public abstract class RouterCommand<TSettings> : AsyncCommand<TSettings>
        where TSettings : RouterSettings
    {
        public override async Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
        {
            using var router = new RouterClient(settings.RouterUrl);
            try
            {
                await Execute(router, settings, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (RouterApiException e)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]{(int)e.StatusCode} {e.StatusCode}:[/] {e.Message}");
                return 1;
            }
            catch (CommandException e)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]{e.Message}[/]");
                return 1;
            }
            catch (HttpRequestException e)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Cannot reach the router at {router.Url}:[/] {e.Message}");
                return 1;
            }
        }

        protected abstract Task Execute(RouterClient router, TSettings settings, CancellationToken cancellationToken);
    }
}
