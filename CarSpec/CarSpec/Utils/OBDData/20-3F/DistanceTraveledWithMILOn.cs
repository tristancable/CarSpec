using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 0121 - Distance traveled with MIL on (km)</summary>
    public sealed class DistanceTraveledWithMILOn : AbstractObdData
    {
        public override string Pid => "0121";
        private int _km;

        public override void Parse(string rawResponse)
        {
            var s = CleanHex(rawResponse);
            if (!Has41Header(s, Pid) || s.Length < 10) { _km = 0; return; }

            var i = s.IndexOf("4121");
            if (i < 0 || s.Length < i + 10) { _km = 0; return; }

            int A = Convert.ToInt32(s.Substring(i + 4, 2), 16);
            int B = Convert.ToInt32(s.Substring(i + 6, 2), 16);
            _km = (A << 8) + B;
        }

        public override void ApplyTo(CarData target) => target.DistanceWithMilKm = _km;
    }
}