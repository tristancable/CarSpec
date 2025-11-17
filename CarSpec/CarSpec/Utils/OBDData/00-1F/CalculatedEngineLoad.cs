using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 0104 — Calculated Engine Load (%)</summary>
    public sealed class CalculatedEngineLoad : AbstractObdData
    {
        public override string Pid => "0104";

        private double? _pct;

        public override void Parse(string rawResponse)
        {
            var clean = CleanHex(rawResponse);
            if (!Has41Header(clean, Pid)) { _pct = null; return; }

            var sig = "4104";
            var idx = clean.IndexOf(sig);
            if (idx < 0 || clean.Length < idx + 6) { _pct = null; return; } // needs AA

            int A = Convert.ToInt32(clean.Substring(idx + 4, 2), 16);
            _pct = (100.0 / 255.0) * A;
        }

        public override void ApplyTo(CarData target) => target.EngineLoadPercent = _pct;
    }
}