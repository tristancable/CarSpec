// Models/VehicleProfile.cs
namespace CarSpec.Models
{
    public class VehicleProfile
    {
        // Identity
        public string Id { get; set; } = Guid.NewGuid().ToString("N");  // NEW
        public string Year { get; set; } = "";
        public string Make { get; set; } = "";
        public string Model { get; set; } = "";
        public string? Engine { get; set; }

        // User prefs
        public string? PreferredAdapterName { get; set; }
        public string? PreferredTransport { get; set; }  // "BLE" | "WIFI" | "USB"

        // Gauges
        public int? TachMaxRpm { get; set; }
        public int? TachRedlineStart { get; set; }
        public int? SpeedMaxMph { get; set; }

        // ECU protocol hints
        public string? ProtocolHint { get; set; }
        public string? ProtocolDetected { get; set; }

        // Optional CAN headers
        public string? CanHeaderTx { get; set; }
        public string? CanHeaderRxFilter { get; set; }

        public List<string> InitScript { get; set; } = new();
        public List<string> DesiredMode01Pids { get; set; } = new();

        // Learned cache
        public List<string>? SupportedPidsCache { get; set; }
        public string? VinCached { get; set; }

        // Alias for older UI (Garage.razor expects VinLast)  // NEW
        public string? VinLast
        {
            get => VinCached;
            set => VinCached = value;
        }
    }
}