namespace CarSpec.Models
{
    public sealed class VehicleProfile
    {
        public string Year { get; init; } = "";
        public string Make { get; init; } = "";
        public string Model { get; init; } = "";
        public string Engine { get; init; } = "";

        // Transport/adapter hints
        public string PreferredTransport { get; init; } = "BLE"; // BLE|WIFI|SERIAL (future)
        public string? PreferredAdapterName { get; init; }        // e.g., "VEEPEAK"

        // Protocol & init hints
        public string ProtocolHint { get; init; } = "AUTO";       // AUTO, ISO9141, CAN11_500, CAN29_500...
        public List<string> InitScript { get; init; } = new()
        {
            "ATE0", "ATL0", "ATS0", "ATAL", "ATAT1", "ATH1"
        };
        public string? CanHeaderTx { get; init; }                 // e.g., "7E0"
        public string? CanHeaderRxFilter { get; init; }           // e.g., "7E8"

        // PIDs to try (Mode 01)
        public List<string> DesiredMode01Pids { get; init; } = new()
        {
            "010C", "010D", "0111", "012F", "0105", "010F", "015C"
        };

        // Gauges
        public int? TachMaxRpm { get; init; }
        public int? TachRedlineStart { get; init; }
        public int? SpeedMaxMph { get; init; }
    }
}