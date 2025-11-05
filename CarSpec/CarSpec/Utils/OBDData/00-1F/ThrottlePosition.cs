using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 0111 - Absolute Throttle Position (%)</summary>
    public sealed class ThrottlePosition : AbstractObdData
    {
        public override string Pid => "0111";

        private double _pct;

        public override void Parse(string rawResponse)
        {
            var clean = CleanHex(rawResponse);
            if (!Has41Header(clean, Pid) || clean.Length < 6) { _pct = 0; return; }

            var idx = clean.IndexOf("4111");
            if (idx < 0 || clean.Length < idx + 6) { _pct = 0; return; }

            int A = Convert.ToInt32(clean.Substring(idx + 4, 2), 16);
            _pct = Math.Round(A * 100.0 / 255.0, 1);
        }

        public override void ApplyTo(CarData target) => target.ThrottlePercent = _pct;
    }
}