namespace NServiceBus.Community.Routing.Model
{
    /// <summary>
    /// Headers used by endpoints and routers.
    /// </summary>
    public static class RoutingHeaders
    {
        public const string DestinationEndpoint = "CentralRouting.DestinationEndpoint";
        public const string DestinationSite = "CentralRouting.DestinationSite";
        public const string ReplyToSite = "CentralRouting.ReplyToSite";
        public const string ReplyToQueue = "CentralRouting.ReplyToQueue";
        public const string Hops = "CentralRouting.Hops";
        public const string Emitter = "CentralRouting.Emitter";
        public const string OriginRouter = "CentralRouting.OriginRouter";
        public const string SnapshotRouter = "CentralRouting.Snapshot.Router";
        public const string SnapshotRevision = "CentralRouting.Snapshot.Revision";
        public const string SnapshotPart = "CentralRouting.Snapshot.Part";
        public const string SnapshotPartCount = "CentralRouting.Snapshot.PartCount";
        public const string FailureReason = "CentralRouting.FailureReason";

        /// <summary>
        /// On a message in a router's holding queue: the queues on the destination site to send it to, as a JSON array.
        /// </summary>
        public const string HeldFor = "CentralRouting.HeldFor";
        public const string ControlMessageType = "CentralRouting.Control.Type";
        public const string ControlMessageVersion = "CentralRouting.Control.Version";
    }

    /// <summary>
    /// Types of control messages sent by endpoints to the control queue of their router.
    /// </summary>
    public static class ControlMessageTypes
    {
        public const string CapabilityReport = "CapabilityReport";
        public const string InstanceStopping = "InstanceStopping";
        public const string PeerSnapshot = "PeerSnapshot";

        /// <summary>
        /// Sent by a router to its own control queue to find out whether a site is reachable. Ignored on receipt.
        /// </summary>
        public const string Probe = "Probe";
    }
}
