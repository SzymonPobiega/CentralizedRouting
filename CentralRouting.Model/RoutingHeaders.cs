namespace CentralRouting.Model
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
    }
}
