namespace CarSpec.Models
{
    public sealed class RecordingMeta
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        public string? VehicleId { get; set; }
        public string? Vehicle { get; set; }     // "2002 Subaru WRX"
        public string? Vin { get; set; }
        public string? Protocol { get; set; }
        public string? Notes { get; set; }

        public int Frames { get; set; }
        public long DurationMs { get; set; }     // from first to last t
        public long ByteSize { get; set; }       // size of gz payload
    }
}
