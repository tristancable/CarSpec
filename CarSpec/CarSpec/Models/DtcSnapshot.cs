namespace CarSpec.Models
{
    public sealed class DtcSnapshot
    {
        public bool MilOn { get; set; }
        public int NumStored { get; set; }

        public List<string> Stored { get; set; } = new();
        public List<string> Pending { get; set; } = new();
        public List<string> Permanent { get; set; } = new();

        public string Raw0101 { get; set; } = "";
        public string Raw03 { get; set; } = "";
        public string Raw07 { get; set; } = "";
        public string Raw0A { get; set; } = "";
    }
}