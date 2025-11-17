// Utils/OBDData/ObdPidCatalog.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace CarSpec.Utils.OBDData
{
    /// <summary>
    /// Central catalog for Mode 01 PIDs → friendly names.
    /// Keep keys as uppercase 4-char hex strings, e.g., "010C".
    /// </summary>
    public static class ObdPidCatalog
    {
        private static readonly IReadOnlyDictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // FAST
                ["010C"] = "Engine RPM",
                ["0111"] = "Throttle Position",
                ["010D"] = "Vehicle Speed",

                // MEDIUM
                ["0105"] = "Coolant Temperature",
                ["010F"] = "Intake Air Temperature",
                ["0104"] = "Calculated Engine Load",
                ["010B"] = "Intake Manifold Pressure (MAP)",
                ["010E"] = "Timing Advance",
                ["0110"] = "MAF Air Flow Rate",
                ["0133"] = "Barometric Pressure",
                ["0122"] = "Fuel Rail Pressure (Relative)",

                // SLOW / OCCASIONAL
                ["012F"] = "Fuel Level",
                ["015C"] = "Engine Oil Temperature",
                ["010A"] = "Fuel Pressure",
                ["0123"] = "Fuel Rail Gauge Pressure",
                ["012C"] = "Commanded EGR",
                ["012D"] = "EGR Error",
                ["012E"] = "Commanded Evap Purge",
                ["0130"] = "Warm-ups Since DTC Clear",
                ["0131"] = "Distance Since DTC Clear",
                ["0132"] = "Evap System Vapor Pressure",
                ["0121"] = "Distance with MIL On",
            };

        /// <summary>Returns the friendly name, or null if unknown.</summary>
        public static string? GetName(string pid) =>
            pid is null ? null : (_map.TryGetValue(pid.Trim().ToUpperInvariant(), out var n) ? n : null);

        /// <summary>Returns a label like "010C — Engine RPM" (falls back to "010C").</summary>
        public static string GetLabel(string pid, string separator = " — ")
        {
            if (string.IsNullOrWhiteSpace(pid)) return "—";
            var key = pid.Trim().ToUpperInvariant();
            return _map.TryGetValue(key, out var name) ? $"{key}{separator}{name}" : key;
        }

        /// <summary>True if we know this PID.</summary>
        public static bool IsKnown(string pid) => GetName(pid) is not null;

        /// <summary>Enumerates PIDs with names for a given list (unknowns included with null name).</summary>
        public static IEnumerable<(string pid, string? name)> LookupMany(IEnumerable<string> pids)
            => (pids ?? Enumerable.Empty<string>())
               .Select(p => (pid: (p ?? "").Trim().ToUpperInvariant(), name: GetName(p)));

        /// <summary>All known entries (PID → name).</summary>
        public static IReadOnlyDictionary<string, string> All => _map;
    }
}