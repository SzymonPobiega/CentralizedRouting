using System;
using NServiceBus.Community.Routing.Model;
using NServiceBus;

/// <summary>
/// The cloud site: the Azure Service Bus namespace in the AzureServiceBus_ConnectionString environment variable.
/// </summary>
static class AzureServiceBusSite
{
    public const string SiteId = "azure";

    public static readonly string ConnectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString")
                                                     ?? throw new Exception("Set the AzureServiceBus_ConnectionString environment variable to the connection string of an Azure Service Bus namespace.");

    public static string Identity => SiteIdentity.ForAzureServiceBus(ConnectionString);

    public static AzureServiceBusTransport CreateTransport() => new AzureServiceBusTransport(ConnectionString, TopicTopology.Default);
}
