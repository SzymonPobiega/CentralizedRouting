using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

/// <summary>
/// The RabbitMQ broker the samples use, by default a container on localhost with the management plugin
/// (<c>docker run -d --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:management</c>).
/// </summary>
static class RabbitMQBroker
{
    public static readonly string Host = Environment.GetEnvironmentVariable("CentralRouting_Sample_RabbitMQHost") ?? "localhost";
    public static readonly string UserName = Environment.GetEnvironmentVariable("CentralRouting_Sample_RabbitMQUser") ?? "guest";
    public static readonly string Password = Environment.GetEnvironmentVariable("CentralRouting_Sample_RabbitMQPassword") ?? "guest";

    public static string ConnectionString(string virtualHost) => $"host={Host};virtualHost={virtualHost};username={UserName};password={Password}";

    /// <summary>
    /// Creates the virtual host through the management API and gives the sample's user full permissions on it.
    /// </summary>
    public static void EnsureVirtualHost(string virtualHost)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{Host}:15672/api/"), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{UserName}:{Password}")));

        try
        {
            var name = Uri.EscapeDataString(virtualHost);
            http.PutAsync($"vhosts/{name}", new StringContent("{}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult().EnsureSuccessStatusCode();
            http.PutAsync($"permissions/{name}/{Uri.EscapeDataString(UserName)}",
                new StringContent("{\"configure\":\".*\",\"write\":\".*\",\"read\":\".*\"}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult().EnsureSuccessStatusCode();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not create the RabbitMQ virtual host '{virtualHost}' through the management API on {Host}:15672 ({e.Message}). " +
                              $"Create it yourself if it doesn't exist: rabbitmqctl add_vhost {virtualHost}; rabbitmqctl set_permissions -p {virtualHost} {UserName} \".*\" \".*\" \".*\"");
        }
    }
}
