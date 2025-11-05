namespace CarSpec.Utils.OBDData
{
    public abstract class AbstractObdData : IObdData
    {
        public abstract string Pid { get; }
        public abstract void Parse(string rawResponse);
        public abstract void ApplyTo(CarSpec.Models.CarData target);

        protected static string CleanHex(string s) =>
            (s ?? string.Empty).Replace("\r", "").Replace("\n", "").Replace(" ", "").ToUpperInvariant();

        protected static bool Has41Header(string clean, string pid) =>
            clean.Contains("41" + pid.Substring(2)); // e.g., for "010C" looks for "410C"
    }
}