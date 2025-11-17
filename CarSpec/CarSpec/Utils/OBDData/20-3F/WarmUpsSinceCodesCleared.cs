using CarSpec.Models;

namespace CarSpec.Utils.OBDData
{
    /// <summary>PID 0130 - Warm-ups since codes cleared</summary>
    public sealed class WarmUpsSinceCodesCleared : AbstractObdData
    {
        public override string Pid => "0130";
        private int _count;

        public override void Parse(string rawResponse)
        {
            var s = CleanHex(rawResponse);
            if (!Has41Header(s, Pid) || s.Length < 8) { _count = 0; return; }

            var i = s.IndexOf("4130");
            if (i < 0 || s.Length < i + 8) { _count = 0; return; }

            int A = Convert.ToInt32(s.Substring(i + 4, 2), 16);
            _count = A;
        }

        public override void ApplyTo(CarData target) => target.WarmUpsSinceClear = _count;
    }
}