using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 010D - Vehicle Speed (mph)</summary>
    public sealed class VehicleSpeed : AbstractObdData
    {
        public override string Pid => "010D";

        private double _mph;

        public override void Parse(string rawResponse)
        {
            // Expect "... 41 0D A ..."
            var clean = CleanHex(rawResponse);
            if (!Has41Header(clean, Pid) || clean.Length < 6) { _mph = 0; return; }

            var idx = clean.IndexOf("410D");
            if (idx < 0 || clean.Length < idx + 6) { _mph = 0; return; }

            int A = Convert.ToInt32(clean.Substring(idx + 4, 2), 16); // km/h
            _mph = Math.Round(A * 0.621371, 1);
        }

        public override void ApplyTo(CarData target) => target.Speed = _mph;
    }
}