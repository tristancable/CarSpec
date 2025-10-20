namespace CarSpec.Utils
{
    /// <summary>
    /// Parses raw OBD-II responses into usable values.
    /// </summary>
    public static class ObdParser
    {
        public static double ParsePidValue(string pid, string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return 0;

            try
            {
                var bytes = response
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .SkipWhile(x => x == "41" || x == pid)
                    .Select(x => Convert.ToByte(x, 16))
                    .ToArray();

                return pid switch
                {
                    "010C" => ((bytes[0] * 256) + bytes[1]) / 4.0, // RPM
                    "010D" => bytes[0],                            // Speed
                    _ => 0
                };
            }
            catch
            {
                return 0;
            }
        }
    }
}