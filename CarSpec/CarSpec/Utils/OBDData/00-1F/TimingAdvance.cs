using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 010E — Timing Advance (°BTDC = A/2 - 64)</summary>
    public sealed class TimingAdvance : AbstractObdData
    {
        public override string Pid => "010E";

        private double? _deg;

        public override void Parse(string rawResponse)
        {
            var clean = CleanHex(rawResponse);
            if (!Has41Header(clean, Pid)) { _deg = null; return; }

            var sig = "410E";
            var idx = clean.IndexOf(sig);
            if (idx < 0 || clean.Length < idx + 6) { _deg = null; return; } // needs AA

            int A = Convert.ToInt32(clean.Substring(idx + 4, 2), 16);
            _deg = (A / 2.0) - 64.0;
        }

        public override void ApplyTo(CarData target) => target.TimingAdvanceDeg = _deg;
    }
}