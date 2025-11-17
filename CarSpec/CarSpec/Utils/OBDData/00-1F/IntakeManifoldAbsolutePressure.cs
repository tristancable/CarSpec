using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 010B — Intake Manifold Absolute Pressure (kPa = A)</summary>
    public sealed class IntakeManifoldAbsolutePressure : AbstractObdData
    {
        public override string Pid => "010B";

        private double? _kpa;

        public override void Parse(string rawResponse)
        {
            var clean = CleanHex(rawResponse);
            if (!Has41Header(clean, Pid)) { _kpa = null; return; }

            var sig = "410B";
            var idx = clean.IndexOf(sig);
            if (idx < 0 || clean.Length < idx + 6) { _kpa = null; return; } // needs AA

            int A = Convert.ToInt32(clean.Substring(idx + 4, 2), 16);
            _kpa = A;
        }

        public override void ApplyTo(CarData target)
        {
            target.MapKpa = _kpa;
            target.MapPsi = _kpa.HasValue ? _kpa.Value * 0.1450377377 : null;
        }
    }
}