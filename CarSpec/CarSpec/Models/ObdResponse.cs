namespace CarSpec.Models
{
    /// <summary>
    /// Represents a raw OBD-II response returned from the ELM327 adapter.
    /// </summary>
    public class ObdResponse
    {
        public string Command { get; set; } = string.Empty;
        public string RawResponse { get; set; } = string.Empty;
        public byte[]? Bytes { get; set; }
    }
}