using System;

namespace CarSpec.Utils
{
    /// <summary>
    /// Stateless helpers to parse ELM327 hex responses into typed values.
    /// Returns safe defaults on errors/unsupported PIDs.
    /// </summary>
    public static class ObdParser
    {
        // ---------- low-level helpers ----------
        public static string CleanHex(string s) =>
            (s ?? string.Empty)
                .Replace("\r", "")
                .Replace("\n", "")
                .Replace(" ", "")
                .Replace(">", "")
                .ToUpperInvariant();

        public static int IndexOfMarker(string clean, string marker)
        {
            var idx = clean.IndexOf(marker, StringComparison.Ordinal);
            return idx >= 0 ? idx : -1;
        }

        // ---------- unit helpers ----------
        public static double CtoF(int c) => Math.Round((c * 9.0 / 5.0) + 32.0, 1);

        // ---------- PID parsers ----------
        // RPM (010C)
        public static int ParseRpm(string response)
        {
            try
            {
                var clean = CleanHex(response);
                var i = IndexOfMarker(clean, "410C");
                if (i < 0 || clean.Length < i + 8) return 0;
                var A = Convert.ToInt32(clean.Substring(i + 4, 2), 16);
                var B = Convert.ToInt32(clean.Substring(i + 6, 2), 16);
                return ((A * 256) + B) / 4;
            }
            catch { return 0; }
        }

        // Speed mph (010D)
        public static int ParseSpeedMph(string response)
        {
            try
            {
                var clean = CleanHex(response);
                var i = IndexOfMarker(clean, "410D");
                if (i < 0 || clean.Length < i + 6) return 0;
                var A = Convert.ToInt32(clean.Substring(i + 4, 2), 16); // km/h
                return (int)Math.Round(A * 0.621371);
            }
            catch { return 0; }
        }

        // Absolute Throttle % (0111)
        public static double ParseThrottlePercent(string response)
        {
            try
            {
                var clean = CleanHex(response);
                var i = IndexOfMarker(clean, "4111");
                if (i < 0 || clean.Length < i + 6) return 0;
                var A = Convert.ToInt32(clean.Substring(i + 4, 2), 16);
                return Math.Round(A * 100.0 / 255.0, 1);
            }
            catch { return 0; }
        }

        // Fuel Level % (012F)
        public static double ParseFuelLevelPercent(string response)
        {
            try
            {
                var clean = CleanHex(response);
                var i = IndexOfMarker(clean, "412F");
                if (i < 0 || clean.Length < i + 6) return 0;
                var A = Convert.ToInt32(clean.Substring(i + 4, 2), 16);
                return Math.Round(A * 100.0 / 255.0, 1);
            }
            catch { return 0; }
        }

        // Coolant Temp °F (0105)
        public static double ParseCoolantF(string response)
        {
            try
            {
                var clean = CleanHex(response);
                var i = IndexOfMarker(clean, "4105");
                if (i < 0 || clean.Length < i + 6) return 0;
                var A = Convert.ToInt32(clean.Substring(i + 4, 2), 16);
                return CtoF(A - 40);
            }
            catch { return 0; }
        }

        // Intake Air Temp °F (010F)
        public static double ParseIntakeF(string response)
        {
            try
            {
                var clean = CleanHex(response);
                var i = IndexOfMarker(clean, "410F");
                if (i < 0 || clean.Length < i + 6) return 0;
                var A = Convert.ToInt32(clean.Substring(i + 4, 2), 16);
                return CtoF(A - 40);
            }
            catch { return 0; }
        }

        // Engine Oil Temp °F (015C) — optional
        public static double ParseOilF(string response)
        {
            try
            {
                var clean = CleanHex(response);
                var i = IndexOfMarker(clean, "415C");
                if (i < 0 || clean.Length < i + 6) return 0; // not supported or no data
                var A = Convert.ToInt32(clean.Substring(i + 4, 2), 16);
                return CtoF(A - 40);
            }
            catch { return 0; }
        }
    }
}