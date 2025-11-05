using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 0105 - Coolant Temp (°F)</summary>
    public sealed class EngineCoolantTemperature : AbstractObdData
    {
        public override string Pid => "0105";

        private double _f;

        public override void Parse(string rawResponse)
        {
            var clean = CleanHex(rawResponse);
            if (!Has41Header(clean, Pid) || clean.Length < 6) { _f = 0; return; }

            var idx = clean.IndexOf("4105");
            if (idx < 0 || clean.Length < idx + 6) { _f = 0; return; }

            int A = Convert.ToInt32(clean.Substring(idx + 4, 2), 16);
            _f = Math.Round(((A - 40) * 9.0 / 5.0) + 32.0, 1);
        }

        public override void ApplyTo(CarData target) => target.CoolantTempF = _f;
    }
}
