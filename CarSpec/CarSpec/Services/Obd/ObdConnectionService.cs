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
        private CancellationTokenSource? _liveCts;

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

        public async Task<bool> AutoConnectAsync()
        {
            if (!await EnsureAdapterAsync())
                return false;

            return await ConnectAsync();
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                IsConnecting = true;
                Log("🔌 Starting ELM327 connection...");

                if (!await EnsureAdapterAsync())
                    return false;

                var ok = await _adapter!.ConnectAsync(); // Elm327Adapter handles init + ECU wake
                IsAdapterConnected = _adapter.IsConnected;
                IsEcuConnected = _adapter.IsEcuAwake;

                if (!ok || !_adapter.IsConnected)
                {
                    Log("❌ Adapter connect failed.");
                    SimulationMode = true;
                    _obdService = null; // ensure null in failure case
                    return false;
                }

                if (!_adapter.IsEcuAwake)
                {
                    Log("⚠️ ECU not responding — staying in Simulation Mode.");
                    SimulationMode = true;
                    _obdService = null; // ECU not up yet; no live service
                    return true;        // adapter is connected, ECU not awake yet
                }

                // ✅ ECU is awake → create the high-level OBD service
                _obdService = new ObdService(_adapter);

                SimulationMode = false;
                Log("✅ Live OBD connected.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ Connect exception: {ex.Message}");
                _obdService = null;
                return false;
            }
            finally
            {
                IsConnecting = false;
            }
        }

        /// <summary>
        /// Build the Elm327Adapter by discovering a compatible BLE device via BluetoothManager.
        /// Tries several common OBD-II adapter names.
        /// </summary>
        private async Task<bool> EnsureAdapterAsync()
        {
            if (_adapter != null) return true;

            Log("🔍 Scanning for ELM327 BLE devices...");

            var pairs = new (string a, string b)[]
            {
        ("VEEPEAK", "OBD"),
        ("OBDII",   "ELM"),
        ("V-LINK",  "OBD"),
            };

            Plugin.BLE.Abstractions.Contracts.IDevice? device = null;

            foreach (var (a, b) in pairs)
            {
                device = await _bluetooth.FindDeviceAsync(a, b);
                if (device != null)
                {
                    Log($"✅ Found device: {device.Name}");
                    break;
                }
            }

            if (device == null)
            {
                Log("❌ No compatible BLE device found.");
                return false;
            }

            // ✔ Pass the device directly; Elm327Adapter will construct BleObdTransport internally
            _adapter = new Elm327Adapter(device);

            // (optional) surface adapter logs through this service
            _adapter.OnLog += Log;

            IsAdapterConnected = false;
            IsEcuConnected = false;
            return true;
        }

        /// <summary>
        /// Send standard ELM327 init sequence and verify OKs where appropriate.
        /// </summary>
        private async Task<bool> InitializeAdapterAsync()
        {
            try
            {
                // Common, safe init sequence for ELM327
                // Reset
                if (!await SendExpectOk("ATZ", allowNoOk: true)) { Log("⚠️ ATZ no OK (often normal)"); }

                // Echo off, linefeeds off, spaces off, headers off, protocol auto
                if (!await SendExpectOk("ATE0")) return false;
                if (!await SendExpectOk("ATL0")) return false;
                if (!await SendExpectOk("ATS0")) return false;
                if (!await SendExpectOk("ATH0")) return false;
                if (!await SendExpectOk("ATSP0")) return false;

                // Optional: increase timeout a bit for some ECUs
                await SendExpectOk("ATST0A", allowNoOk: true);

                Log("✅ Adapter initialized.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ InitializeAdapterAsync error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Try a simple Mode 01 PID (0100) to confirm ECU is awake/responding.
        /// </summary>
        private async Task<bool> ConfirmEcuAwakeAsync()
        {
            try
            {
                var resp = await _adapter!.SendCommandAsync("0100");
                var raw = (resp?.RawResponse ?? string.Empty).ToUpperInvariant();

                // Typical success contains "41 00 ..." (available PIDs)
                if (raw.Contains("41") && raw.Contains("00"))
                    return true;

                // Some adapters add "NO DATA" if ignition off
                if (raw.Contains("NO DATA") || string.IsNullOrWhiteSpace(raw))
                    return false;

                // Fallback: any hex bytes back that aren't pure prompts may be OK
                return raw.Any(ch => "0123456789ABCDEF".Contains(ch));
            }
            catch (Exception ex)
            {
                Log($"❌ ConfirmEcuAwakeAsync error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Helper: send a command and check if "OK" appears (ELM327 style).
        /// </summary>
        private async Task<bool> SendExpectOk(string cmd, bool allowNoOk = false)
        {
            var resp = await _adapter!.SendCommandAsync(cmd);
            var raw = (resp?.RawResponse ?? string.Empty).ToUpperInvariant();

            if (raw.Contains("OK")) return true;
            if (allowNoOk) return true;

            // Some firmwares omit OK after reset; accept a non-empty, non-error line
            if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("?") && !raw.Contains("ERROR"))
                return true;

            Log($"❌ Expected OK for '{cmd}', got: '{raw.Trim()}'");
            return false;
        }

        // New overload: streams a full CarData snapshot each tick
        public async Task StartLiveDataLoopAsync(Action<CarData>? onData = null)
        {
            // cancel any prior loop
            CancelLiveLoop();
            _liveCts = new CancellationTokenSource();
            var token = _liveCts.Token;

            if (_adapter == null || !_adapter.IsConnected || SimulationMode)
            {
                Log("⚠️ Cannot start live data loop — adapter not connected or in Simulation Mode.");
                return;
            }

            Log("📡 Starting live data polling (full CarData)...");
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // snapshot the adapter reference to avoid races with _adapter = null
                    var adapter = _adapter;
                    if (adapter == null || !adapter.IsConnected || SimulationMode)
                        break;

                    try
                    {
                        // Request set (pace gently)
                        var r010C = await adapter.SendCommandAsync("010C"); // RPM
                        if (token.IsCancellationRequested) break; await Task.Delay(150, token);

                        var r010D = await adapter.SendCommandAsync("010D"); // Speed
                        if (token.IsCancellationRequested) break; await Task.Delay(150, token);

                        var r0111 = await adapter.SendCommandAsync("0111"); // Throttle %
                        if (token.IsCancellationRequested) break; await Task.Delay(150, token);

                        var r012F = await adapter.SendCommandAsync("012F"); // Fuel %
                        if (token.IsCancellationRequested) break; await Task.Delay(150, token);

                        var r0105 = await adapter.SendCommandAsync("0105"); // Coolant °C
                        if (token.IsCancellationRequested) break; await Task.Delay(150, token);

                        var r010F = await adapter.SendCommandAsync("010F"); // IAT °C
                        if (token.IsCancellationRequested) break; await Task.Delay(150, token);

                        var r015C = await adapter.SendCommandAsync("015C"); // Oil °C (may be unsupported)

                        var cd = new CarData
                        {
                            RPM = ParseRpm(r010C.RawResponse),
                            Speed = ParseSpeed(r010D.RawResponse),
                            ThrottlePercent = ParseThrottle(r0111.RawResponse),
                            FuelLevelPercent = ParseFuelLevel(r012F.RawResponse),
                            CoolantTempF = ParseCoolantF(r0105.RawResponse),
                            IntakeTempF = ParseIntakeF(r010F.RawResponse),
                            OilTempF = ParseOilF(r015C.RawResponse),
                            LastUpdated = DateTime.Now
                        };

                        onData?.Invoke(cd);
                    }
                    catch (TaskCanceledException) { break; }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException)
                    {
                        // adapter disposed during disconnect
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ Live data read error: {ex.Message}");
                    }

                    try { await Task.Delay(600, token); } catch { break; }
                }
            }
            finally
            {
                Log("🛑 Live data loop ended.");
            }
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

                // Ensure service exists once ECU is responding again
                if (_obdService == null)
                    _obdService = new ObdService(_adapter);

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

        private void CancelLiveLoop()
        {
            try { _liveCts?.Cancel(); } catch { /* ignore */ }
            _liveCts = null;
        }

        public Task Disconnect()
        {
            // stop polling first
            CancelLiveLoop();

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

        // ---- Shared helpers ----
        private static string CleanHex(string s) =>
            (s ?? string.Empty).Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace(">", "").ToUpperInvariant();

        private static int IndexOfMarker(string clean, string marker)
        {
            var idx = clean.IndexOf(marker, StringComparison.Ordinal);
            return idx >= 0 ? idx : -1;
        }

        // ---- RPM (010C) ----
        private int ParseRpm(string response)
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

        // ---- Speed mph (010D) ----
        private int ParseSpeed(string response)
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

        // ---- Throttle % (0111) ---- Absolute Throttle Position
        private double ParseThrottle(string response)
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

        // ---- Fuel Level % (012F) ----
        private double ParseFuelLevel(string response)
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

        // Convert °C to °F
        private static double CtoF(int c) => Math.Round((c * 9.0 / 5.0) + 32.0, 1);

        // ---- Coolant Temp °F (0105) ----
        private double ParseCoolantF(string response)
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

        // ---- Intake Air Temp °F (010F) ----
        private double ParseIntakeF(string response)
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

        // ---- Engine Oil Temp °F (015C) ---- (not all ECUs support this)
        private double ParseOilF(string response)
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

        private void Log(string msg)
        {
            _log.Info(msg);
            OnLog?.Invoke(msg);
        }
    }
}