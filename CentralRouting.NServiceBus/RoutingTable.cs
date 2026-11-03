using System;
using CentralRouting.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using NServiceBus.Logging;

namespace CentralRouting.NServiceBus
{
    record RouteToSiteOption(string Site);

    /// <summary>
    /// The routing document currently in use, with the endpoint's own site resolved from its identity.
    /// Swapped atomically when the configuration reloads.
    /// </summary>
    class RoutingTable
    {
        static readonly ILog log = LogManager.GetLogger<RoutingTable>();

        readonly IConfiguration configuration;
        readonly string siteIdentity;
        volatile RoutingTableState state;

        public RoutingTable(IConfiguration configuration, string siteIdentity)
        {
            this.configuration = configuration;
            this.siteIdentity = siteIdentity;
            Load();
            ChangeToken.OnChange(configuration.GetReloadToken, Load);
        }

        public bool IsLoaded => state != null;

        public long Revision => state?.Resolver.Document.Revision ?? 0;

        public RoutingTableState Current => state ?? throw new Exception(
            "No routing document is available. The router could not be reached and there is no cached document.");

        void Load()
        {
            var json = configuration[RoutingDocumentConfigurationProvider.DocumentKey];
            if (json == null)
            {
                return;
            }

            var document = RoutingJson.Deserialize<RoutingDocument>(json);
            if (state != null && state.Resolver.Document.Revision == document.Revision)
            {
                return;
            }

            var resolver = new AddressResolver(document);
            var localSite = resolver.FindSite(siteIdentity);
            state = new RoutingTableState(resolver, localSite, siteIdentity);

            log.Info($"Using routing document revision {document.Revision} from router {document.Router}. Local site: {localSite ?? "unknown"}.");
        }
    }

    class RoutingTableState
    {
        readonly string siteIdentity;

        public RoutingTableState(AddressResolver resolver, string localSite, string siteIdentity)
        {
            Resolver = resolver;
            LocalSite = localSite;
            this.siteIdentity = siteIdentity;
        }

        public AddressResolver Resolver { get; }
        public string LocalSite { get; }

        /// <summary>
        /// Returns the address of the endpoint's own router on the local site.
        /// </summary>
        public string GetRouterAddress()
        {
            EnsureLocalSite();
            return Resolver.Document.Sites[LocalSite].Router;
        }

        /// <summary>
        /// Returns the address to send to: the destination queue on the local site, or the router's queue that leads to the destination site.
        /// </summary>
        public string GetAddress(Destination destination, out bool viaRouter)
        {
            EnsureLocalSite();
            viaRouter = destination.Site != LocalSite;
            return viaRouter
                ? Resolver.NextHop(LocalSite, destination.Site)
                : Resolver.ChooseQueue(destination.Endpoint, destination.Site);
        }

        void EnsureLocalSite()
        {
            if (LocalSite == null)
            {
                throw new RoutingException($"The routing document has no site with identity '{siteIdentity}'. " +
                                           "The router learns it from the endpoint's first report; the next reload will pick it up.");
            }
        }
    }
}
