namespace CarSpec.Models
{
    // CarSpec.Models.VehicleProfile
    public sealed class VehicleProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string? Year { get; set; }
        public string? Make { get; set; }
        public string? Model { get; set; }
        public string? Engine { get; set; }

        // User hints
        public string? PreferredTransport { get; set; }     // learned after first connect
        public string? PreferredAdapterName { get; set; }   // user-entered nickname

        // Learned/cached
        public string? ProtocolHint { get; set; }           // (optional) manual hint
        public string? ProtocolDetected { get; set; }       // learned
        public List<string>? InitScript { get; set; }       // optional AT lines
        public List<string>? DesiredMode01Pids { get; set; }
        public List<string>? SupportedPidsCache { get; set; }
        public string? VinLast { get; set; }
        public string? WmiLast { get; set; }
        public int? YearDetected { get; set; }
        public List<string>? CalIds { get; set; }
        public DateTime? LastConnectedUtc { get; set; }

        // Gauges
        public int? TachMaxRpm { get; set; }
        public int? TachRedlineStart { get; set; }
        public int? SpeedMaxMph { get; set; }

        // 🔹 Add these two OPTIONAL CAN header props
        public string? CanHeaderTx { get; set; }
        public string? CanHeaderRxFilter { get; set; }
    }
}