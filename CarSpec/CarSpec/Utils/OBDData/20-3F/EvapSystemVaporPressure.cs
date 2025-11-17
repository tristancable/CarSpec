using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 0132 - Evap System Vapor Pressure (Pa)</summary>
    public sealed class EvapSystemVaporPressure : AbstractObdData
    {
        public override string Pid => "0132";
        private int _pa;

        public override void Parse(string rawResponse)
        {
            var s = CleanHex(rawResponse);
            if (!Has41Header(s, Pid) || s.Length < 10) { _pa = 0; return; }

            var i = s.IndexOf("4132");
            if (i < 0 || s.Length < i + 10) { _pa = 0; return; }

            int A = Convert.ToInt32(s.Substring(i + 4, 2), 16);
            int B = Convert.ToInt32(s.Substring(i + 6, 2), 16);
            // value = ((A*256)+B) - 32768  (Pa)
            _pa = ((A << 8) + B) - 32768;
        }

        public override void ApplyTo(CarData target) => target.EvapVaporPressurePa = _pa;
    }
}