using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NServiceBus.Community.Routing.Model
{
    /// <summary>
    /// Destination of a message: an endpoint on a site.
    /// </summary>
    public record Destination(string Endpoint, string Site);

    public class RoutingException : Exception
    {
        public RoutingException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Turns the routing document into destinations. Used by endpoints to pick the site and by routers to pick the queue.
    /// The same document always yields the same decisions, so both sides of a hop agree.
    /// </summary>
    public class AddressResolver
    {
        readonly ConcurrentDictionary<string, long> counters = new ConcurrentDictionary<string, long>();

        public AddressResolver(RoutingDocument document)
        {
            Document = document;
        }

        public RoutingDocument Document { get; }

        public string FindSite(string siteIdentity)
        {
            return Document.Sites
                .Where(s => s.Value.Identities.Contains(siteIdentity, StringComparer.OrdinalIgnoreCase))
                .Select(s => s.Key)
                .FirstOrDefault();
        }

        public string NextHop(string fromSite, string toSite)
        {
            if (fromSite != null
                && Document.Sites.TryGetValue(fromSite, out var site)
                && site.NextHop.TryGetValue(toSite, out var queue))
            {
                return queue;
            }

            throw new RoutingException($"No next hop from site '{fromSite}' to site '{toSite}'.");
        }

        /// <summary>
        /// Returns the destination for a command, or null when the type has no route.
        /// </summary>
        public Destination ResolveCommand(string messageType, string localSite, string requestedSite)
        {
            if (!Document.Routes.TryGetValue(messageType, out var route))
            {
                return null;
            }

            return new Destination(route.Endpoint, ChooseSite(route.Endpoint, localSite, requestedSite));
        }

        /// <summary>
        /// Picks the site of an endpoint according to its address policy.
        /// </summary>
        public string ChooseSite(string endpoint, string localSite, string requestedSite)
        {
            var info = GetEndpoint(endpoint);
            var sites = LiveSites(info);
            if (sites.Count == 0)
            {
                throw new RoutingException($"Endpoint '{endpoint}' has no live instances.");
            }

            if (requestedSite != null)
            {
                if (!sites.Contains(requestedSite))
                {
                    throw new RoutingException($"Endpoint '{endpoint}' has no live instances on site '{requestedSite}'.");
                }
                return requestedSite;
            }

            switch (info.Policy)
            {
                case AddressPolicy.Strict:
                    return sites.Contains(localSite) ? localSite : sites[0];
                case AddressPolicy.PreferLocal:
                    return sites.Contains(localSite) ? localSite : RoundRobin($"site:{endpoint}", sites);
                case AddressPolicy.Distribute:
                    return WeightedRoundRobin($"site:{endpoint}", sites.ToDictionary(s => s, s => info.Addresses[s].Values.Sum()));
                case AddressPolicy.Explicit:
                    throw new RoutingException($"Endpoint '{endpoint}' uses the Explicit address policy. Specify the destination site with RouteToSite.");
                case AddressPolicy.NewestSite:
                    //The router publishes only the selected site
                    return sites[0];
                default:
                    throw new RoutingException($"Unsupported address policy {info.Policy}.");
            }
        }

        /// <summary>
        /// Picks one of the endpoint's queues on a site, weighted by the number of instances behind each queue.
        /// </summary>
        public string ChooseQueue(string endpoint, string site)
        {
            var info = GetEndpoint(endpoint);
            if (!info.Addresses.TryGetValue(site, out var queues) || queues.Values.Sum() == 0)
            {
                throw new RoutingException($"Endpoint '{endpoint}' has no live instances on site '{site}'.");
            }

            return WeightedRoundRobin($"queue:{endpoint}:{site}", queues);
        }

        /// <summary>
        /// Returns the subscribers of an event and the site each should receive it on.
        /// The publisher delivers to targets on its own site and sends one copy to its router for all others.
        /// The router, called with the site the copy arrived on, delivers to the targets on the other sites.
        /// Only the published type's own route counts: subscribers are assigned to the type that is published, not to its
        /// base types, so delivery never depends on the type hierarchy.
        /// Explicit endpoints are never targets: each of their sites is a separate business destination, which events
        /// can't address. Routers refuse such subscriptions, and validation reports any left over.
        /// </summary>
        public IReadOnlyList<Destination> ResolveEvent(string eventType, string sourceSite)
        {
            var subscribers = Document.Events.TryGetValue(eventType, out var route)
                ? route.Subscribers.Distinct().OrderBy(s => s, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

            var targets = new List<Destination>();
            foreach (var subscriber in subscribers)
            {
                if (!Document.Endpoints.TryGetValue(subscriber, out var info) || info.Policy == AddressPolicy.Explicit)
                {
                    continue;
                }

                var sites = LiveSites(info);
                if (sites.Count == 0)
                {
                    continue;
                }

                if (sites.Contains(sourceSite))
                {
                    targets.Add(new Destination(subscriber, sourceSite));
                }
                else
                {
                    targets.Add(new Destination(subscriber, ChooseSite(subscriber, null, null)));
                }
            }
            return targets;
        }

        public EventRoute GetEventRoute(string messageType)
        {
            return Document.Events.TryGetValue(messageType, out var route) ? route : null;
        }

        EndpointInfo GetEndpoint(string endpoint)
        {
            if (!Document.Endpoints.TryGetValue(endpoint, out var info))
            {
                throw new RoutingException($"Endpoint '{endpoint}' is not known to the router.");
            }
            return info;
        }

        static List<string> LiveSites(EndpointInfo info)
        {
            return info.Addresses
                .Where(a => a.Value.Values.Sum() > 0)
                .Select(a => a.Key)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
        }

        string RoundRobin(string key, IReadOnlyList<string> items)
        {
            var counter = counters.AddOrUpdate(key, 0, (_, v) => v + 1);
            return items[(int)(counter % items.Count)];
        }

        string WeightedRoundRobin(string key, IReadOnlyDictionary<string, int> weights)
        {
            var ordered = weights.Where(w => w.Value > 0).OrderBy(w => w.Key, StringComparer.Ordinal).ToList();
            var total = ordered.Sum(w => w.Value);
            var counter = counters.AddOrUpdate(key, 0, (_, v) => v + 1);
            var position = (int)(counter % total);
            foreach (var item in ordered)
            {
                if (position < item.Value)
                {
                    return item.Key;
                }
                position -= item.Value;
            }
            return ordered[^1].Key;
        }
    }
}
