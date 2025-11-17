using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 0133 - Absolute Barometric Pressure (kPa)</summary>
    public sealed class AbsoluteBarometricPressure : AbstractObdData
    {
        public override string Pid => "0133";
        private int _kpa;

        public override void Parse(string rawResponse)
        {
            var s = CleanHex(rawResponse);
            if (!Has41Header(s, Pid) || s.Length < 8) { _kpa = 0; return; }

            var i = s.IndexOf("4133");
            if (i < 0 || s.Length < i + 8) { _kpa = 0; return; }

            int A = Convert.ToInt32(s.Substring(i + 4, 2), 16);
            _kpa = A; // direct kPa
        }

        public override void ApplyTo(CarData target) => target.BaroKPa = _kpa;
    }
}