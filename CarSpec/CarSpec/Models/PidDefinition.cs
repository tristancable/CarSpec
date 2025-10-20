namespace CarSpec.Models
{
    /// <summary>
    /// Defines a known OBD-II PID with its mode, PID code, and description.
    /// </summary>
    public class PidDefinition
    {
        public string Mode { get; set; } = "01";
        public string Pid { get; set; } = "0C"; // default: RPM
        public string Description { get; set; } = "Engine RPM";
        public Func<byte[], double>? Parse { get; set; }
    }
}