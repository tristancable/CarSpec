using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 0110 — MAF Air Flow Rate (g/s)</summary>
    public sealed class MAFAirFlowRate : AbstractObdData
    {
        public override string Pid => "0110";

        private double? _gPerSec;

        public override void Parse(string rawResponse)
        {
            var clean = CleanHex(rawResponse);
            if (!Has41Header(clean, Pid)) { _gPerSec = null; return; }

            var sig = "4110";
            var idx = clean.IndexOf(sig);
            if (idx < 0 || clean.Length < idx + 8) { _gPerSec = null; return; } // needs AABB

            int A = Convert.ToInt32(clean.Substring(idx + 4, 2), 16);
            int B = Convert.ToInt32(clean.Substring(idx + 6, 2), 16);
            _gPerSec = ((A * 256.0) + B) / 100.0;
        }

        public override void ApplyTo(CarData target) => target.MafGramsPerSec = _gPerSec;
    }
}