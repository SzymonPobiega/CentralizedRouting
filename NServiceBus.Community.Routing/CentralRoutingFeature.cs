using System;
using System.Linq;
using NServiceBus.Community.Routing.Model;
using NServiceBus.Community.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NServiceBus.Features;
using NServiceBus.Hosting;
using NServiceBus.Settings;
using NServiceBus.Transport;
using NServiceBus.Unicast;
using NServiceBus.Unicast.Messages;

// Features are always defined in NServiceBus namespace
// ReSharper disable once CheckNamespace
namespace NServiceBus
{
    class CentralRoutingFeature : Feature
    {
        protected override void Setup(FeatureConfigurationContext context)
        {
            var settings = context.Settings.Get<CentralRoutingSettings>();
            var endpointName = context.Settings.EndpointName();
            var transport = context.Settings.Get<TransportDefinition>();
            var siteIdentity = settings.SiteIdentity ?? DetectSiteIdentity(transport);

            var configuration = settings.Configuration
                                ?? new ConfigurationBuilder().AddCentralRouting(o => Copy(settings.Document, o)).Build();
            var documentProvider = (configuration as IConfigurationRoot)?.Providers.OfType<RoutingDocumentConfigurationProvider>().FirstOrDefault();

            var routingTable = new RoutingTable(configuration, siteIdentity);
            context.Services.AddSingleton(routingTable);

            context.Pipeline.Replace("UnicastSendRouterConnector",
                sp => new SendRoutingConnector(routingTable, sp.GetService<ReceiveAddresses>(), sp.GetRequiredService<ITransportAddressResolver>(), endpointName),
                "Routes commands with the central routing document");
            context.Pipeline.Replace(transport.SupportsPublishSubscribe ? "MulticastPublishRouterBehavior" : "UnicastPublishRouterConnector",
                sp => new PublishRoutingConnector(routingTable, endpointName),
                "Routes events with the central routing document");
            context.Pipeline.Register(new PublishDeclarationBehavior(settings.PublishedTypes, endpointName), "Refuses to publish events the endpoint does not declare");
            context.Pipeline.Register(new ReplyToPropagationBehavior(), "Lets replies to messages that crossed sites find their way back");
            context.Pipeline.Register(new SubscribeNotSupportedBehavior(), "Subscriptions are managed by the router");
            context.Pipeline.Register(new UnsubscribeNotSupportedBehavior(), "Subscriptions are managed by the router");

            context.RegisterStartupTask(sp =>
            {
                var addressResolver = sp.GetRequiredService<ITransportAddressResolver>();
                var report = BuildReport(context.Settings, settings, endpointName, siteIdentity,
                    sp.GetRequiredService<HostInformation>(), sp.GetService<ReceiveAddresses>());

                return new CapabilityReporter(report, settings, routingTable, documentProvider,
                    sp.GetRequiredService<IMessageDispatcher>(),
                    addressResolver.ToTransportAddress(new QueueAddress($"{settings.RouterName}.Control")));
            });
        }

        static CapabilityReport BuildReport(IReadOnlySettings endpointSettings, CentralRoutingSettings settings, string endpointName,
            string siteIdentity, HostInformation hostInformation, ReceiveAddresses receiveAddresses)
        {
            var conventions = endpointSettings.Get<Conventions>();
            var handlerRegistry = endpointSettings.Get<MessageHandlerRegistry>();
            var metadataRegistry = endpointSettings.Get<MessageMetadataRegistry>();

            var handledTypes = handlerRegistry.GetMessageTypes()
                .Where(t => !conventions.IsInSystemConventionList(t))
                .Select(t => new HandledType
                {
                    Type = t.FullName,
                    Kind = conventions.IsEventType(t) ? MessageKind.Event : conventions.IsCommandType(t) ? MessageKind.Command : MessageKind.Message,
                    BaseTypes = metadataRegistry.GetMessageMetadata(t).MessageHierarchy.Where(b => b != t).Select(b => b.FullName).ToList(),
                    Handlers = handlerRegistry.GetHandlersFor(t).Select(h => h.HandlerType.FullName).Distinct().OrderBy(h => h).ToList()
                })
                .OrderBy(t => t.Type, StringComparer.Ordinal)
                .ToList();

            var publishedTypes = settings.PublishedTypes
                .Select(t =>
                {
                    if (!conventions.IsEventType(t))
                    {
                        throw new Exception($"{t.FullName} is declared as published by {endpointName} but is not an event.");
                    }
                    return new PublishedType
                    {
                        Type = t.FullName,
                        BaseTypes = metadataRegistry.GetMessageMetadata(t).MessageHierarchy.Where(b => b != t).Select(b => b.FullName).ToList()
                    };
                })
                .OrderBy(t => t.Type, StringComparer.Ordinal)
                .ToList();

            return new CapabilityReport
            {
                EndpointName = endpointName,
                InstanceId = settings.InstanceId ?? hostInformation.HostId.ToString(),
                HostName = hostInformation.DisplayName,
                InputQueue = receiveAddresses?.MainReceiveAddress,
                SiteIdentity = siteIdentity,
                HandledTypes = handledTypes,
                PublishedTypes = publishedTypes,
                CapabilityHash = CapabilityReporter.ComputeHash(handledTypes, publishedTypes)
            };
        }

        static string DetectSiteIdentity(TransportDefinition transport)
        {
            if (transport is LearningTransport learning && learning.StorageDirectory != null)
            {
                return SiteIdentity.ForLearningTransport(learning.StorageDirectory);
            }

            throw new Exception("The site identity of this endpoint could not be detected from its transport. Set it with UseCentralRouting(...).SiteIdentity.");
        }

        static void Copy(RoutingDocumentSourceOptions source, RoutingDocumentSourceOptions target)
        {
            target.RouterUrl = source.RouterUrl;
            target.Cache = source.Cache;
            target.ReloadInterval = source.ReloadInterval;
            target.RequestTimeout = source.RequestTimeout;
            target.HttpMessageHandler = source.HttpMessageHandler;
        }
    }
}
