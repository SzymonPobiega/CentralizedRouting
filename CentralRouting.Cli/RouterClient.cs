using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralRouting.Model;

namespace CentralRouting.Cli
{
    /// <summary>
    /// Calls the router's HTTP API, version 1.
    /// </summary>
    public class RouterClient : IDisposable
    {
        readonly HttpClient http;

        public RouterClient(Uri routerUrl)
        {
            var baseUrl = routerUrl.AbsoluteUri.EndsWith('/') ? routerUrl : new Uri(routerUrl.AbsoluteUri + "/");
            http = new HttpClient { BaseAddress = new Uri(baseUrl, "api/v1/") };
            Url = routerUrl;
        }

        public Uri Url { get; }

        /// <summary>
        /// Builds a relative API path, escaping each segment. Message types may contain characters such as '+'.
        /// </summary>
        public static string Path(params string[] segments) => string.Join('/', segments.Select(Uri.EscapeDataString));

        public Task<T> Get<T>(string path, CancellationToken cancellationToken) => Send<T>(HttpMethod.Get, path, null, cancellationToken);

        public Task<T> Put<T>(string path, object body, CancellationToken cancellationToken) => Send<T>(HttpMethod.Put, path, body, cancellationToken);

        public Task<T> Post<T>(string path, object body, CancellationToken cancellationToken) => Send<T>(HttpMethod.Post, path, body, cancellationToken);

        public Task<T> Delete<T>(string path, CancellationToken cancellationToken) => Send<T>(HttpMethod.Delete, path, null, cancellationToken);

        public Task Delete(string path, CancellationToken cancellationToken) => Send<object>(HttpMethod.Delete, path, null, cancellationToken);

        async Task<T> Send<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body != null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: RoutingJson.Options);
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new RouterApiException(response.StatusCode, await ReadError(response, cancellationToken).ConfigureAwait(false));
            }

            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
            {
                return default;
            }

            return await response.Content.ReadFromJsonAsync<T>(RoutingJson.Options, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The API reports conflicts as a JSON string and other errors as problem details or an empty body.
        /// </summary>
        static async Task<string> ReadError(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return response.StatusCode == HttpStatusCode.NotFound ? "Not found." : response.ReasonPhrase;
            }

            try
            {
                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    return root.GetString();
                }
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                    {
                        return detail.GetString();
                    }
                    if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                    {
                        return title.GetString();
                    }
                }
            }
            catch (JsonException)
            {
            }

            return text;
        }

        public void Dispose()
        {
            http.Dispose();
        }
    }

    /// <summary>
    /// An error detected by the tool itself, reported without a status code.
    /// </summary>
    public class CommandException : Exception
    {
        public CommandException(string message)
            : base(message)
        {
        }
    }

    public class RouterApiException : Exception
    {
        public RouterApiException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }
}
