using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 012D - EGR Error (%)</summary>
    public sealed class EGRError : AbstractObdData
    {
        public override string Pid => "012D";
        private double _pct;

        public override void Parse(string rawResponse)
        {
            var s = CleanHex(rawResponse);
            if (!Has41Header(s, Pid) || s.Length < 8) { _pct = 0; return; }

            var i = s.IndexOf("412D");
            if (i < 0 || s.Length < i + 8) { _pct = 0; return; }

            int A = Convert.ToInt32(s.Substring(i + 4, 2), 16);
            _pct = Math.Round((A - 128) * 100.0 / 128.0, 1); // negative allowed
        }

        public override void ApplyTo(CarData target) => target.EgrErrorPercent = _pct;
    }
}