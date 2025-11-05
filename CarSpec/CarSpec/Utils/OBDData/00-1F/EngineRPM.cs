// File: Utils/OBDData/00-14/EngineRPM.cs
using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 010C - Engine RPM</summary>
    public sealed class EngineRPM : AbstractObdData
    {
        public override string Pid => "010C";

        private double _rpm;

        public override void Parse(string rawResponse)
        {
            // Expect "... 41 0C A B ..."
            var clean = CleanHex(rawResponse);
            if (!Has41Header(clean, Pid) || clean.Length < 8) { _rpm = 0; return; }

            // Find "410C"
            var idx = clean.IndexOf("410C");
            if (idx < 0 || clean.Length < idx + 8) { _rpm = 0; return; }

            int A = Convert.ToInt32(clean.Substring(idx + 4, 2), 16);
            int B = Convert.ToInt32(clean.Substring(idx + 6, 2), 16);
            _rpm = ((A * 256) + B) / 4.0;
        }

        public override void ApplyTo(CarData target) => target.RPM = _rpm;
    }
}