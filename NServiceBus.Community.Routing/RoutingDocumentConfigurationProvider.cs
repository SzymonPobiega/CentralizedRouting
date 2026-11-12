using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Model;
using Microsoft.Extensions.Configuration;
using NServiceBus.Logging;

namespace NServiceBus.Community.Routing
{
    /// <summary>
    /// Where and how an endpoint gets its routing document.
    /// </summary>
    public class RoutingDocumentSourceOptions
    {
        /// <summary>
        /// Base URL of the router's HTTP API.
        /// </summary>
        public Uri RouterUrl { get; set; }

        /// <summary>
        /// Keeps the last received document for starting when the router is unreachable. Optional.
        /// </summary>
        public IRoutingDocumentCache Cache { get; set; }

        /// <summary>
        /// How often the document is checked for changes. <see cref="Timeout.InfiniteTimeSpan"/> turns reload off.
        /// </summary>
        public TimeSpan ReloadInterval { get; set; } = TimeSpan.FromSeconds(60);

        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Custom handler for the HTTP requests, e.g. to add authentication. Optional.
        /// </summary>
        public HttpMessageHandler HttpMessageHandler { get; set; }
    }

    class RoutingDocumentConfigurationSource : IConfigurationSource
    {
        readonly RoutingDocumentSourceOptions options;

        public RoutingDocumentConfigurationSource(RoutingDocumentSourceOptions options)
        {
            this.options = options;
        }

        public IConfigurationProvider Build(IConfigurationBuilder builder) => new RoutingDocumentConfigurationProvider(options);
    }

    /// <summary>
    /// Exposes the routing document as configuration. The document is fetched from the router at load time,
    /// falls back to the cache when the router is unreachable, and is polled for changes.
    /// </summary>
    public class RoutingDocumentConfigurationProvider : ConfigurationProvider, IDisposable
    {
        public const string DocumentKey = "CentralRouting:Document";
        public const string RevisionKey = "CentralRouting:Revision";

        static readonly ILog log = LogManager.GetLogger<RoutingDocumentConfigurationProvider>();

        readonly RoutingDocumentSourceOptions options;
        readonly HttpClient httpClient;
        readonly CancellationTokenSource stopping = new CancellationTokenSource();
        readonly SemaphoreSlim fetchLock = new SemaphoreSlim(1, 1);
        string etag;
        Task pollLoop;

        public RoutingDocumentConfigurationProvider(RoutingDocumentSourceOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            if (options.RouterUrl == null)
            {
                throw new ArgumentException("The router URL is required.", nameof(options));
            }

            httpClient = options.HttpMessageHandler != null ? new HttpClient(options.HttpMessageHandler, false) : new HttpClient();
            httpClient.BaseAddress = options.RouterUrl;
            httpClient.Timeout = options.RequestTimeout;
        }

        public override void Load()
        {
            try
            {
                FetchDocument(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                log.Warn($"Could not fetch the routing document from {options.RouterUrl}.", e);
            }

            if (!Data.ContainsKey(DocumentKey))
            {
                var cached = options.Cache?.Load();
                if (cached != null)
                {
                    log.Warn("Using the cached routing document.");
                    SetDocument(cached);
                }
            }

            if (pollLoop == null && options.ReloadInterval != Timeout.InfiniteTimeSpan)
            {
                pollLoop = Task.Run(() => Poll(stopping.Token));
            }
        }

        /// <summary>
        /// Checks the router for a newer document now. Returns true when the document changed.
        /// </summary>
        public async Task<bool> Refresh(CancellationToken cancellationToken)
        {
            if (!await FetchDocument(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            OnReload();
            return true;
        }

        async Task<bool> FetchDocument(CancellationToken cancellationToken)
        {
            await fetchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/routing-document");
                if (etag != null)
                {
                    request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
                }

                using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return false;
                }

                response.EnsureSuccessStatusCode();
                var document = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                etag = response.Headers.ETag?.ToString();

                SetDocument(document);
                options.Cache?.Save(document);
                return true;
            }
            finally
            {
                fetchLock.Release();
            }
        }

        void SetDocument(string document)
        {
            var revision = RoutingJson.Deserialize<RoutingDocument>(document).Revision;
            Data[DocumentKey] = document;
            Data[RevisionKey] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        async Task Poll(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(options.ReloadInterval, cancellationToken).ConfigureAwait(false);
                    await Refresh(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    log.Warn($"Could not refresh the routing document from {options.RouterUrl}.", e);
                }
            }
        }

        public void Dispose()
        {
            stopping.Cancel();
            httpClient.Dispose();
        }
    }
}
