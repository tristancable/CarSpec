using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 0123 - Fuel Rail Gauge Pressure (kPa)</summary>
    public sealed class FuelRailGaugePressure : AbstractObdData
    {
        public override string Pid => "0123";
        private int _kpa;

        public override void Parse(string rawResponse)
        {
            var s = CleanHex(rawResponse);
            if (!Has41Header(s, Pid) || s.Length < 10) { _kpa = 0; return; }

            var i = s.IndexOf("4123");
            if (i < 0 || s.Length < i + 10) { _kpa = 0; return; }

            int A = Convert.ToInt32(s.Substring(i + 4, 2), 16);
            int B = Convert.ToInt32(s.Substring(i + 6, 2), 16);
            // standard: value = 10 * (A*256 + B) kPa
            _kpa = 10 * ((A << 8) + B);
        }

        public override void ApplyTo(CarData target) => target.FuelRailGaugePressureKPa = _kpa;
    }
}