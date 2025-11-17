using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 0122 - Fuel Rail Pressure (relative to manifold vacuum) [kPa]</summary>
    public sealed class FuelRailPressure : AbstractObdData
    {
        public override string Pid => "0122";
        private double _kpa;

        public override void Parse(string rawResponse)
        {
            var s = CleanHex(rawResponse);
            if (!Has41Header(s, Pid) || s.Length < 10) { _kpa = 0; return; }

            var i = s.IndexOf("4122");
            if (i < 0 || s.Length < i + 10) { _kpa = 0; return; }

            int A = Convert.ToInt32(s.Substring(i + 4, 2), 16);
            int B = Convert.ToInt32(s.Substring(i + 6, 2), 16);
            // standard: value = 0.079 * (A*256 + B) kPa
            _kpa = Math.Round(0.079 * ((A << 8) + B), 1);
        }

        public override void ApplyTo(CarData target) => target.FuelRailPressureRelKPa = _kpa;
    }
}