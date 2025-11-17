using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 010A — Fuel Pressure (kPa = 3*A)</summary>
    public sealed class FuelPressure : AbstractObdData
    {
        public override string Pid => "010A";

        private double? _kpa;

        public override void Parse(string rawResponse)
        {
            var clean = CleanHex(rawResponse);
            if (!Has41Header(clean, Pid)) { _kpa = null; return; }

            var sig = "410A";
            var idx = clean.IndexOf(sig);
            if (idx < 0 || clean.Length < idx + 6) { _kpa = null; return; } // needs AA

            int A = Convert.ToInt32(clean.Substring(idx + 4, 2), 16);
            _kpa = 3.0 * A;
        }

        public override void ApplyTo(CarData target)
        {
            target.FuelPressureKpa = _kpa;
            target.FuelPressurePsi = _kpa.HasValue ? _kpa.Value * 0.1450377377 : null;
        }
    }
}