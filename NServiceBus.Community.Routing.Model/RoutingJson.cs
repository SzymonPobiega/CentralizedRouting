using System.Text.Json;
using System.Text.Json.Serialization;

namespace NServiceBus.Community.Routing.Model
{
    /// <summary>
    /// Serializer settings shared by the router, its API and the endpoints.
    /// </summary>
    public static class RoutingJson
    {
        public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        public static JsonSerializerOptions Configure(JsonSerializerOptions options)
        {
            options.Converters.Add(new JsonStringEnumConverter());
            options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            return options;
        }

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

        public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

        public static T Deserialize<T>(byte[] json) => JsonSerializer.Deserialize<T>(json, Options);
    }
}
