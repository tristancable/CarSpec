using CarSpec.Models;
using CarSpec.Utils;

namespace CarSpec.Services.Obd
{
    /// <summary>
    /// Handles initialization of the ELM327 and reading vehicle data.
    /// </summary>
    public class ObdService
    {
        private readonly Elm327Adapter _adapter;
        private readonly Logger _log = new("OBD");

        private readonly string[] _initCommands =
        {
            "ATZ",      // Reset
            "ATE0",     // Echo off
            "ATL0",     // Linefeed off
            "ATS0",     // Spaces off
            "ATH0",     // Headers off
            "ATSP 0",   // Auto protocol
            "ATSI",     // Start slow init (for ISO 9141-2)
            "ATST FF",  // Extend timeout (Subaru ECUs are slow)
            "0100"      // Query supported PIDs (wake ECU)
        };

        public ObdService(Elm327Adapter adapter)
        {
            _adapter = adapter;
        }

        public async Task<bool> InitializeAsync()
        {
            _log.Info("🔧 Starting ELM327 initialization sequence...");

            foreach (var cmd in _initCommands)
            {
                _log.Info($"➡️ Sending: {cmd}");
                var response = await _adapter.SendCommandAsync(cmd);

                if (string.IsNullOrWhiteSpace(response.RawResponse))
                {
                    _log.Warn($"⚠️ No response for {cmd} — ECU may still be asleep.");
                    await Task.Delay(1000);
                    continue;
                }

                _log.Info($"⬅️ Response for {cmd}: {response.RawResponse}");
                await Task.Delay(500); // Give ECU breathing room
            }

            // Verify ECU is awake
            _log.Info("🔍 Verifying ECU communication...");
            var check = await _adapter.SendCommandAsync("0100");
            if (check.RawResponse.Contains("41"))
            {
                _log.Info($"✅ ECU communication confirmed → {check.RawResponse}");
                return true;
            }
            else
            {
                _log.Warn($"⚠️ ECU not responding properly → {check.RawResponse}");
                return false;
            }
        }

        public async Task<CarData> GetLatestDataAsync()
        {
            var rpmResp = await _adapter.SendCommandAsync("010C");
            await Task.Delay(300); // wait between reads

            var speedResp = await _adapter.SendCommandAsync("010D");
            await Task.Delay(300);

            double rpm = ParseRpm(rpmResp.RawResponse);
            double speed = ParseSpeed(speedResp.RawResponse);

            _log.Info($"📈 Live Data → RPM: {rpm:F0}, Speed: {speed:F1} mph");

            return new CarData
            {
                RPM = rpm,
                Speed = speed,
                LastUpdated = DateTime.Now
            };
        }

        private double ParseRpm(string raw)
        {
            if (raw.Contains("41 0C"))
            {
                var parts = raw.Split(' ')
                    .Where(p => p.Length == 2 && int.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out _))
                    .Select(p => Convert.ToInt32(p, 16))
                    .ToArray();

                if (parts.Length >= 4)
                {
                    int A = parts[2];
                    int B = parts[3];
                    return (A * 256 + B) / 4.0;
                }
            }
            return 0;
        }

        private double ParseSpeed(string raw)
        {
            if (raw.Contains("41 0D"))
            {
                var parts = raw.Split(' ')
                    .Where(p => p.Length == 2 && int.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out _))
                    .Select(p => Convert.ToInt32(p, 16))
                    .ToArray();

                if (parts.Length >= 3)
                {
                    int A = parts[2];
                    return A * 0.621371; // km/h → mph
                }
            }
            return 0;
        }

        private async Task<double> GetPidAsync(string pid, string expectedHeader)
        {
            var response = await _adapter.SendCommandAsync(pid);

            if (string.IsNullOrWhiteSpace(response.RawResponse))
            {
                _log.Warn($"⚠️ Empty response for PID {pid}");
                return 0;
            }

            if (!response.RawResponse.Contains(expectedHeader))
            {
                _log.Warn($"⚠️ No ECU data (engine likely off or ECU asleep) — PID {pid} → {response.RawResponse}");
                return 0;
            }

            try
            {
                return ObdParser.ParsePidValue(pid, response.RawResponse);
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to parse PID {pid}: {ex.Message}");
                return 0;
            }
        }
    }
}