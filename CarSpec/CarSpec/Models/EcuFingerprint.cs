using System;
using System.Collections.Generic;

namespace CarSpec.Models
{
    public sealed class EcuFingerprint
    {
        public string? Vin { get; init; }
        public string? Wmi { get; init; }          // first 3 of VIN
        public int? Year { get; init; }            // decoded from VIN[9]
        public string? Protocol { get; init; }     // from ATDP (e.g., "ISO 15765-4 (CAN 11/500)")
        public HashSet<string> SupportedPids { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CalIds { get; init; } = new();

        public override string ToString() =>
            $"VIN={Vin ?? "?"}, Year={Year?.ToString() ?? "?"}, Proto={Protocol ?? "?"}, PIDs={SupportedPids.Count}";
    }
}