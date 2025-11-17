using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 0131 - Distance traveled since codes cleared (km)</summary>
    public sealed class DistanceTraveledSinceCodesCleared : AbstractObdData
    {
        public override string Pid => "0131";
        private int _km;

        public override void Parse(string rawResponse)
        {
            var s = CleanHex(rawResponse);
            if (!Has41Header(s, Pid) || s.Length < 10) { _km = 0; return; }

            var i = s.IndexOf("4131");
            if (i < 0 || s.Length < i + 10) { _km = 0; return; }

            int A = Convert.ToInt32(s.Substring(i + 4, 2), 16);
            int B = Convert.ToInt32(s.Substring(i + 6, 2), 16);
            _km = (A << 8) + B;
        }

        public override void ApplyTo(CarData target) => target.DistanceSinceClearKm = _km;
    }
}