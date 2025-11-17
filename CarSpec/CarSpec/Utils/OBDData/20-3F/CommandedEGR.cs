using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 012C - Commanded EGR (%)</summary>
    public sealed class CommandedEGR : AbstractObdData
    {
        public override string Pid => "012C";
        private double _pct;

        public override void Parse(string rawResponse)
        {
            var s = CleanHex(rawResponse);
            if (!Has41Header(s, Pid) || s.Length < 8) { _pct = 0; return; }

            var i = s.IndexOf("412C");
            if (i < 0 || s.Length < i + 8) { _pct = 0; return; }

            int A = Convert.ToInt32(s.Substring(i + 4, 2), 16);
            _pct = Math.Round(A * 100.0 / 255.0, 1);
        }

        public override void ApplyTo(CarData target) => target.CommandedEgrPercent = _pct;
    }
}