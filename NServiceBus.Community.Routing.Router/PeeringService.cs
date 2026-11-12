using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Community.Routing.Model;
using Microsoft.Extensions.Logging;
using NServiceBus;
using NServiceBus.Transport;

namespace NServiceBus.Community.Routing.Router
{
    /// <summary>
    /// Exchanges snapshots with peer routers and runs the subscription request and grant protocol.
    /// </summary>
    class PeeringService : IDisposable
    {
        readonly RouterConfiguration configuration;
        readonly IRouterStore store;
        readonly RoutingSnapshotProvider snapshots;
        readonly RouterTopology topology;
        readonly ILogger logger;
        readonly Dictionary<string, HttpClient> httpClients = new Dictionary<string, HttpClient>();
        readonly ConcurrentDictionary<string, (long Revision, DateTimeOffset At)> lastSent = new ConcurrentDictionary<string, (long, DateTimeOffset)>();
        readonly ConcurrentDictionary<(string Peer, long Revision), byte[][]> stagedParts = new ConcurrentDictionary<(string, long), byte[][]>();
        readonly SemaphoreSlim applyLock = new SemaphoreSlim(1, 1);

        public PeeringService(RouterConfiguration configuration, IRouterStore store, RoutingSnapshotProvider snapshots, RouterTopology topology,
            ILogger<PeeringService> logger)
        {
            this.configuration = configuration;
            this.store = store;
            this.snapshots = snapshots;
            this.topology = topology;
            this.logger = logger;

            foreach (var peer in configuration.Peers.Where(p => p.Channel == PeerChannel.Http))
            {
                var client = peer.HttpMessageHandler != null ? new HttpClient(peer.HttpMessageHandler, false) : new HttpClient();
                client.BaseAddress = peer.Url;
                httpClients[peer.Name] = client;
            }
        }

        /// <summary>
        /// Sends the snapshot to every peer that has not seen the current revision, and to all of them once per heartbeat interval.
        /// </summary>
        public async Task SendSnapshots(CancellationToken cancellationToken)
        {
            var current = snapshots.Current;
            foreach (var peer in configuration.Peers.Where(p => p.Channel != PeerChannel.Inbound))
            {
                var due = !lastSent.TryGetValue(peer.Name, out var sent)
                          || sent.Revision != current.Revision
                          || DateTimeOffset.UtcNow - sent.At >= configuration.PeerHeartbeatInterval;
                if (!due)
                {
                    continue;
                }

                try
                {
                    await Send(peer, CreateSnapshot(current), cancellationToken).ConfigureAwait(false);
                    lastSent[peer.Name] = (current.Revision, DateTimeOffset.UtcNow);
                }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(e, "Could not send the snapshot to peer {Peer}", peer.Name);
                }
            }
        }

        async Task Send(PeerConfiguration peer, PeerSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (peer.Channel == PeerChannel.Http)
            {
                var response = await httpClients[peer.Name].PostAsJsonAsync("api/v1/peering/exchange", snapshot, RoutingJson.Options, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var peerSnapshot = await response.Content.ReadFromJsonAsync<PeerSnapshot>(RoutingJson.Options, cancellationToken).ConfigureAwait(false);
                await Apply(peerSnapshot, cancellationToken).ConfigureAwait(false);
                return;
            }

            var routerInterface = topology.GetInterfaceByName(peer.InterfaceName);
            var address = routerInterface.ToTransportAddress($"{peer.Name}.Control");
            var parts = Split(Compress(RoutingJson.Serialize(snapshot)), routerInterface.Configuration.MaxSnapshotPartSize);

            var messages = parts.Select((part, index) =>
            {
                var messageId = Guid.NewGuid().ToString();
                var headers = new Dictionary<string, string>
                {
                    [Headers.MessageId] = messageId,
                    [RoutingHeaders.ControlMessageType] = ControlMessageTypes.PeerSnapshot,
                    [RoutingHeaders.SnapshotRouter] = snapshot.Router,
                    [RoutingHeaders.SnapshotRevision] = snapshot.Revision.ToString(CultureInfo.InvariantCulture),
                    [RoutingHeaders.SnapshotPart] = index.ToString(CultureInfo.InvariantCulture),
                    [RoutingHeaders.SnapshotPartCount] = parts.Count.ToString(CultureInfo.InvariantCulture)
                };
                return (address, new OutgoingMessage(messageId, headers, part));
            });

            await routerInterface.Dispatch(messages, new TransportTransaction(), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Handles a call of a peer to this router's HTTP API: applies its snapshot and returns this router's.
        /// </summary>
        public async Task<PeerSnapshot> Exchange(PeerSnapshot snapshot, CancellationToken cancellationToken)
        {
            await Apply(snapshot, cancellationToken).ConfigureAwait(false);
            var current = snapshots.Current;
            lastSent[snapshot.Router] = (current.Revision, DateTimeOffset.UtcNow);
            return CreateSnapshot(current);
        }

        public bool IsPeer(string name) => configuration.Peers.Any(p => p.Name == name);

        /// <summary>
        /// Stages a part of a snapshot received on a control queue. Parts can arrive in any order and more than once.
        /// </summary>
        public async Task ReceivePart(IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            var peer = headers[RoutingHeaders.SnapshotRouter];
            var revision = long.Parse(headers[RoutingHeaders.SnapshotRevision], CultureInfo.InvariantCulture);
            var index = int.Parse(headers[RoutingHeaders.SnapshotPart], CultureInfo.InvariantCulture);
            var count = int.Parse(headers[RoutingHeaders.SnapshotPartCount], CultureInfo.InvariantCulture);

            var parts = stagedParts.GetOrAdd((peer, revision), _ => new byte[count][]);
            lock (parts)
            {
                parts[index] = body.ToArray();
                if (parts.Any(p => p == null))
                {
                    return;
                }
            }

            //A newer revision replaces half-assembled older ones
            foreach (var key in stagedParts.Keys.Where(k => k.Peer == peer && k.Revision <= revision))
            {
                stagedParts.TryRemove(key, out _);
            }

            var snapshot = RoutingJson.Deserialize<PeerSnapshot>(Decompress(parts.SelectMany(p => p).ToArray()));
            await Apply(snapshot, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Replaces everything known about the peer with its snapshot and updates the subscription requests between the two routers.
        /// </summary>
        public async Task Apply(PeerSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (snapshot.Router == configuration.Name)
            {
                logger.LogWarning("Ignoring a snapshot that claims to come from this router ({Router})", snapshot.Router);
                return;
            }

            if (!IsPeer(snapshot.Router))
            {
                logger.LogWarning("Ignoring a snapshot from {Router}, which is not a configured peer", snapshot.Router);
                return;
            }

            await applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var peers = await store.GetPeers(cancellationToken).ConfigureAwait(false);
                var peer = peers.FirstOrDefault(p => p.Name == snapshot.Router) ?? new PeerRecord { Name = snapshot.Router };
                if (peer.Snapshot != null && snapshot.SentAt < peer.Snapshot.SentAt)
                {
                    return;
                }

                var changed = peer.Snapshot == null || Content(peer.Snapshot) != Content(snapshot);
                peer.Snapshot = snapshot;
                peer.LastReceivedAt = DateTimeOffset.UtcNow;
                await store.SavePeer(peer, cancellationToken).ConfigureAwait(false);

                changed |= await ApplyRequests(snapshot, cancellationToken).ConfigureAwait(false);
                if (changed)
                {
                    await snapshots.Changed(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                applyLock.Release();
            }
        }

        async Task<bool> ApplyRequests(PeerSnapshot snapshot, CancellationToken cancellationToken)
        {
            var changed = false;
            var requests = await store.GetSubscriptionRequests(cancellationToken).ConfigureAwait(false);

            //Requests of the peer addressed to this router
            var requested = snapshot.Requests.Where(r => r.Router == configuration.Name).Select(r => r.MessageType).ToHashSet();
            foreach (var incoming in requests.Where(r => r.Direction == SubscriptionDirection.Incoming && r.Peer == snapshot.Router))
            {
                if (!requested.Remove(incoming.MessageType))
                {
                    //Withdrawn by the peer
                    await store.DeleteSubscriptionRequest(incoming, cancellationToken).ConfigureAwait(false);
                    changed = true;
                }
            }
            foreach (var messageType in requested)
            {
                await store.SaveSubscriptionRequest(new SubscriptionRequestRecord
                {
                    MessageType = messageType,
                    Peer = snapshot.Router,
                    Direction = SubscriptionDirection.Incoming,
                    Status = SubscriptionRequestStatus.Requested,
                    RequestedAt = DateTimeOffset.UtcNow
                }, cancellationToken).ConfigureAwait(false);
                changed = true;
            }

            //Grants of the peer for this router's requests
            var granted = snapshot.Grants.Where(g => g.Router == configuration.Name).Select(g => g.MessageType).ToHashSet();
            foreach (var outgoing in requests.Where(r => r.Direction == SubscriptionDirection.Outgoing && r.Peer == snapshot.Router))
            {
                var status = granted.Contains(outgoing.MessageType) ? SubscriptionRequestStatus.Granted : SubscriptionRequestStatus.Requested;
                if (outgoing.Status != status)
                {
                    outgoing.Status = status;
                    outgoing.GrantedAt = status == SubscriptionRequestStatus.Granted ? DateTimeOffset.UtcNow : null;
                    await store.SaveSubscriptionRequest(outgoing, cancellationToken).ConfigureAwait(false);
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// Moves incoming requests towards a grant. A request for an event this router has a publisher for becomes Granting:
        /// the next document revision tells the publisher to send a copy to the router. The grant completes when every live
        /// instance of the publisher runs that revision, so no event is lost once the peer's operator subscribes.
        /// </summary>
        public async Task CompleteGrants(CancellationToken cancellationToken)
        {
            var model = snapshots.Current.Model;
            foreach (var request in model.Requests.Where(r => r.Direction == SubscriptionDirection.Incoming))
            {
                model.Routes.TryGetValue(request.MessageType, out var route);
                var publisher = route?.Publisher;

                if (publisher == null || !model.IsOwned(publisher))
                {
                    if (request.Status != SubscriptionRequestStatus.Requested)
                    {
                        request.Status = SubscriptionRequestStatus.Requested;
                        request.GrantRevision = 0;
                        await store.SaveSubscriptionRequest(request, cancellationToken).ConfigureAwait(false);
                        await snapshots.Changed(cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                if (request.Status == SubscriptionRequestStatus.Requested)
                {
                    request.Status = SubscriptionRequestStatus.Granting;
                    await store.SaveSubscriptionRequest(request, cancellationToken).ConfigureAwait(false);

                    var snapshot = await snapshots.Changed(cancellationToken).ConfigureAwait(false);
                    request.GrantRevision = snapshot.Revision;
                    await store.SaveSubscriptionRequest(request, cancellationToken).ConfigureAwait(false);
                    await snapshots.Refresh(true, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (request.Status == SubscriptionRequestStatus.Granting && request.GrantRevision > 0)
                {
                    //During a migration both publishers may publish, so both have to forward
                    var instances = model.RoutableInstances(model.Endpoints[publisher]).ToList();
                    if (route.PreviousPublisher != null && model.Endpoints.TryGetValue(route.PreviousPublisher, out var previous))
                    {
                        instances.AddRange(model.RoutableInstances(previous));
                    }
                    if (instances.All(i => i.DocumentRevision >= request.GrantRevision))
                    {
                        request.Status = SubscriptionRequestStatus.Granted;
                        request.GrantedAt = DateTimeOffset.UtcNow;
                        await store.SaveSubscriptionRequest(request, cancellationToken).ConfigureAwait(false);
                        await snapshots.Changed(cancellationToken).ConfigureAwait(false);
                        logger.LogInformation("Granted {Peer} the subscription request for {MessageType}", request.Peer, request.MessageType);
                    }
                }
            }
        }

        PeerSnapshot CreateSnapshot(RoutingSnapshot current)
        {
            var snapshot = current.Model.BuildSnapshot(current.Revision);
            snapshot.SentAt = DateTimeOffset.UtcNow;
            return snapshot;
        }

        static string Content(PeerSnapshot snapshot)
        {
            var sentAt = snapshot.SentAt;
            var revision = snapshot.Revision;
            snapshot.SentAt = default;
            snapshot.Revision = 0;
            var content = RoutingJson.Serialize(snapshot);
            snapshot.SentAt = sentAt;
            snapshot.Revision = revision;
            return content;
        }

        static byte[] Compress(string json)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            {
                gzip.Write(System.Text.Encoding.UTF8.GetBytes(json));
            }
            return output.ToArray();
        }

        static byte[] Decompress(byte[] data)
        {
            using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }

        static List<byte[]> Split(byte[] data, int partSize)
        {
            var parts = new List<byte[]>();
            for (var offset = 0; offset < data.Length; offset += partSize)
            {
                parts.Add(data.AsSpan(offset, Math.Min(partSize, data.Length - offset)).ToArray());
            }
            return parts;
        }

        public void Dispose()
        {
            foreach (var client in httpClients.Values)
            {
                client.Dispose();
            }
        }
    }
}
