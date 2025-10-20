namespace CarSpec.Services.Obd
{
    /// <summary>
    /// Optional class that tries ATSP1–6 to find the working OBD protocol.
    /// </summary>
    public class ProtocolDetector
    {
        private static readonly string[] Protocols = { "ATSP0", "ATSP1", "ATSP2", "ATSP3", "ATSP4", "ATSP5", "ATSP6" };

        public async Task<string> DetectAsync(Func<string, Task<string>> send)
        {
            foreach (var p in Protocols)
            {
                var response = await send(p);
                if (!response.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return "ATSP0";
        }
    }
}