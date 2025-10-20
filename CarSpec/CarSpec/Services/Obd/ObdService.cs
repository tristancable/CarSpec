using CarSpec.Models;
using CarSpec.Utils;
using System;
using System.Threading.Tasks;

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
            "ATZ",   // Reset
            "ATE0",  // Echo off
            "ATL0",  // Linefeed off
            "ATS0",  // Spaces off
            "ATH0",  // Headers off
            "ATSP0"  // Auto protocol
        };

        public ObdService(Elm327Adapter adapter)
        {
            _adapter = adapter;
        }

        public async Task InitializeAsync()
        {
            foreach (var cmd in _initCommands)
            {
                var resp = await _adapter.SendCommandAsync(cmd);
                if (string.IsNullOrWhiteSpace(resp.RawResponse) || resp.RawResponse.Contains("ERROR"))
                {
                    _log.Warn($"⚠️ Init command failed: {cmd} → {resp.RawResponse}");
                }

                await Task.Delay(250);
            }

            _log.Info("✅ ELM327 initialization complete.");
        }

        public async Task<CarData> GetLatestDataAsync()
        {
            try
            {
                double rpm = await GetPidAsync("010C", "41 0C");
                double speed = await GetPidAsync("010D", "41 0D");

                return new CarData
                {
                    RPM = rpm,
                    Speed = speed,
                    LastUpdated = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                _log.Error($"Unhandled exception while reading OBD data: {ex.Message}");
                return CarData.Simulated(); // fallback
            }
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