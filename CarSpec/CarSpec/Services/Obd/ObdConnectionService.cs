using CarSpec.Models;
using CarSpec.Services.Bluetooth;
using CarSpec.Utils;
using Microsoft.Extensions.Logging;

namespace CarSpec.Services.Obd
{
    /// <summary>
    /// High-level orchestrator for connecting to ELM327 and reading data.
    /// </summary>
    public class ObdConnectionService
    {
        private readonly BluetoothManager _bluetooth;
        private Elm327Adapter? _adapter;
        private ObdService? _obdService;
        private readonly Logger _log = new("OBD-CONNECT");

        public bool SimulationMode { get; set; } = true;
        public bool IsConnected => _adapter?.IsConnected ?? false;
        public bool IsConnecting { get; private set; }
        public bool IsAdapterConnected { get; private set; }
        public bool IsEcuConnected { get; private set; }

        public event Action<string>? OnLog;

        public ObdConnectionService()
        {
            _bluetooth = new BluetoothManager();
            _bluetooth.OnLog += Log;
        }

        public async Task<bool> AutoConnectAsync() => await ConnectAsync();

        public async Task<bool> ConnectAsync()
        {
            IsConnecting = true;
            IsAdapterConnected = false;
            IsEcuConnected = false;
            SimulationMode = true;

            try
            {
                if (!_bluetooth.IsOn)
                {
                    Log("⚠️ Bluetooth is turned off — please enable it to connect.");
                    return false;
                }

                Log("🔍 Scanning for VEEPEAK/OBD...");
                var device = await _bluetooth.FindDeviceAsync("VEEPEAK", "OBD");

                if (device == null)
                {
                    Log("⚠️ No OBD device found.");
                    return false;
                }

                _adapter = new Elm327Adapter(device);
                _adapter.OnLog += Log;

                // --- Step 1: Connect to adapter ---
                if (!await _adapter.ConnectAsync())
                {
                    Log("❌ Failed to connect to adapter.");
                    return false;
                }

                IsAdapterConnected = true;
                SimulationMode = false;
                Log("✅ Connected to ELM327 adapter.");
                await _adapter.SendCommandAsync("010C");
                await _adapter.SendCommandAsync("010D");

                // --- Step 2: Verify ECU communication ---
                Log("🔎 Checking ECU communication...");

                bool ecuAwake = false;

                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    var ecuResp = await _adapter.SendCommandAsync("0100");
                    var cleaned = ecuResp.RawResponse?
                        .Replace("\r", "")
                        .Replace("\n", "")
                        .Replace(">", "")
                        .Replace(" ", "")
                        .ToUpperInvariant() ?? "";

                    Log($"🔍 ECU Attempt {attempt} → {cleaned}");

                    if (cleaned.Contains("4100") || cleaned.Contains("41") && cleaned.Length >= 6)
                    {
                        ecuAwake = true;
                        SimulationMode = false;
                        IsEcuConnected = true;
                        Log($"✅ ECU communication established on attempt {attempt}! → {cleaned}");
                        break;
                    }
                    else if (cleaned.Contains("BUSINIT") || cleaned.Contains("STOPPED") || cleaned.Contains("SEARCHING"))
                    {
                        Log($"⏳ Attempt {attempt}: ECU initializing → {cleaned}");
                        await Task.Delay(1500);
                        continue;
                    }
                    else
                    {
                        Log($"⚠️ Attempt {attempt}: ECU not ready → {cleaned}");
                        await Task.Delay(1000);
                    }
                }

                IsEcuConnected = ecuAwake;
                SimulationMode = !ecuAwake;

                if (ecuAwake)
                    Log("✅ ECU communication confirmed! Live ECU Mode enabled.");
                else
                    Log("⚠️ ECU not responding — staying in Simulation Mode.");

                return true;
            }
            catch (Exception ex)
            {
                Log($"[Error] Connection failed: {ex.Message}");
                SimulationMode = true;
                return false;
            }
            finally
            {
                IsConnecting = false;
            }
        }

        // Continuously reads live data once ECU connection is active
        public async Task StartLiveDataLoopAsync(Action<int, int>? onDataReceived = null)
        {
            if (_adapter == null || !_adapter.IsConnected || !IsEcuConnected)
            {
                Log("⚠️ Cannot start live data loop — ECU not connected.");
                return;
            }

            _obdService = new ObdService(_adapter); // create or reuse your service

            Log("📡 Starting live data polling...");

            while (IsEcuConnected && !SimulationMode)
            {
                try
                {
                    // Request Engine RPM (PID 010C)
                    var rpmResp = await _adapter.SendCommandAsync("010C");
                    var rpm = ParseRpm(rpmResp.RawResponse);

                    await Task.Delay(200);

                    // Request Vehicle Speed (PID 010D)
                    var speedResp = await _adapter.SendCommandAsync("010D");
                    var speed = ParseSpeed(speedResp.RawResponse);

                    Log($"📈 Live Data → RPM: {rpm}, Speed: {speed} km/h");

                    // Trigger UI update callback
                    onDataReceived?.Invoke(rpm, speed);
                }
                catch (Exception ex)
                {
                    Log($"⚠️ Live data read error: {ex.Message}");
                }

                await Task.Delay(1500); // 1-second refresh interval
            }

            Log("🛑 Live data loop ended.");
        }

        public async Task<bool> TryReconnectEcuAsync()
        {
            if (_adapter == null)
            {
                Log("⚠️ No adapter connected — cannot reconnect ECU.");
                return false;
            }

            Log("🔁 Attempting ECU reconnection...");
            var resp = await _adapter.SendCommandAsync("0100");

            if (!string.IsNullOrWhiteSpace(resp.RawResponse) && !resp.RawResponse.Contains("NO_DATA"))
            {
                Log("✅ ECU reconnected successfully!");
                IsEcuConnected = true;
                SimulationMode = false;
                return true;
            }

            Log("⚠️ ECU still not responding — car may be off.");
            return false;
        }

        public async Task<CarData> GetLatestDataAsync()
        {
            if (SimulationMode || _obdService == null)
                return CarData.Simulated();

            return await _obdService.GetLatestDataAsync();
        }

        public Task Disconnect()
        {
            if (_adapter != null)
            {
                try
                {
                    if (_adapter.IsConnected)
                    {
                        _adapter.Disconnect();
                        Log("🔌 Disconnected from ELM327 adapter.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"⚠️ Error during disconnect: {ex.Message}");
                }

                _adapter = null;
            }

            IsAdapterConnected = false;
            IsEcuConnected = false;
            SimulationMode = true;

            Log("🧹 Disconnection complete, back to Simulation Mode.");
            return Task.CompletedTask;
        }

        private int ParseRpm(string response)
        {
            try
            {
                // Expected format: 410CXXXX (A=B1, B=B2)
                var clean = response.Replace(" ", "").Replace(">", "").ToUpperInvariant();
                if (!clean.StartsWith("410C") || clean.Length < 8) return 0;

                var A = Convert.ToInt32(clean.Substring(4, 2), 16);
                var B = Convert.ToInt32(clean.Substring(6, 2), 16);
                return ((A * 256) + B) / 4;
            }
            catch { return 0; }
        }

        private int ParseSpeed(string response)
        {
            try
            {
                // Expected format: 410DXX
                var clean = response.Replace(" ", "").Replace(">", "").ToUpperInvariant();
                if (!clean.StartsWith("410D") || clean.Length < 6) return 0;

                var A = Convert.ToInt32(clean.Substring(4, 2), 16);
                return A; // km/h
            }
            catch { return 0; }
        }

        private void Log(string msg)
        {
            _log.Info(msg);
            OnLog?.Invoke(msg);
        }
    }
}