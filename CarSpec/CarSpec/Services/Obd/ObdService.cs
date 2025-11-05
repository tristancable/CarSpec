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
            "ATST FF",  // Extend timeout (some ECUs are slow)
            "0100"      // Query supported PIDs (wake ECU)
        };

        public ObdService(Elm327Adapter adapter) => _adapter = adapter;

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

            _log.Warn($"⚠️ ECU not responding properly → {check.RawResponse}");
            return false;
        }

        public async Task<CarData> GetLatestDataAsync()
        {
            var r010C = await _adapter.SendCommandAsync("010C"); // RPM
            await Task.Delay(300);
            var r010D = await _adapter.SendCommandAsync("010D"); // Speed
            await Task.Delay(300);
            var r0111 = await _adapter.SendCommandAsync("0111"); // Throttle %
            await Task.Delay(300);
            var r012F = await _adapter.SendCommandAsync("012F"); // Fuel %
            await Task.Delay(300);
            var r0105 = await _adapter.SendCommandAsync("0105"); // Coolant °C → °F
            await Task.Delay(300);
            var r010F = await _adapter.SendCommandAsync("010F"); // IAT °C → °F
            await Task.Delay(300);
            var r015C = await _adapter.SendCommandAsync("015C"); // Oil °C → °F (may be unsupported)

            var cd = new CarData
            {
                RPM = ObdParser.ParseRpm(r010C.RawResponse),
                Speed = ObdParser.ParseSpeedMph(r010D.RawResponse),
                ThrottlePercent = ObdParser.ParseThrottlePercent(r0111.RawResponse),
                FuelLevelPercent = ObdParser.ParseFuelLevelPercent(r012F.RawResponse),
                CoolantTempF = ObdParser.ParseCoolantF(r0105.RawResponse),
                IntakeTempF = ObdParser.ParseIntakeF(r010F.RawResponse),
                OilTempF = ObdParser.ParseOilF(r015C.RawResponse),
                LastUpdated = DateTime.Now
            };

            _log.Info($"📈 Live Data → " +
                      $"RPM: {cd.RPM:F0}, " +
                      $"Speed: {cd.Speed:F0} mph, " +
                      $"TPS: {cd.ThrottlePercent:F1}%, " +
                      $"Fuel: {cd.FuelLevelPercent:F1}%, " +
                      $"Coolant: {cd.CoolantTempF:F0}°F, " +
                      $"IAT: {cd.IntakeTempF:F0}°F, " +
                      $"Oil: {cd.OilTempF:F0}°F");

            return cd;
        }
    }
}