using CarSpec.Models;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Text;
using System.Threading;

namespace CarSpec.Services.Obd
{
    public class ObdConnectionServiceOriginal
    {
        private readonly IBluetoothLE _ble;
        private readonly IAdapter _adapter;
        private IDevice? _device;
        private ICharacteristic? _writeCharacteristic;  // TX
        private ICharacteristic? _notifyCharacteristic; // RX
        private readonly Random _rand = new();
        private readonly SemaphoreSlim _pidLock = new(1, 1);

        public bool IsConnecting { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsLiveConnected => IsConnected && !SimulationMode;
        public bool SimulationMode { get; set; } = true;

        private CarData _latestData = new();
        private readonly Dictionary<string, DateTime> _pidCooldowns = new();
        private readonly Dictionary<string, int> _pidFailures = new();
        private readonly TimeSpan _cooldown = TimeSpan.FromMilliseconds(500);
        //private readonly int _maxFailures = 3;

        private readonly Dictionary<string, string> _lastValues = new();
        private readonly HashSet<string> _failedPids = new();
        private HashSet<string> _supportedPids = new();
        private readonly string[] _pidsToRead = { "010D", "010C", "0111", "012F" };

        public event Action<string>? OnLog;

        public ObdConnectionServiceOriginal()
        {
            _ble = CrossBluetoothLE.Current;
            _adapter = CrossBluetoothLE.Current.Adapter;
        }

        private void Log(string message) => OnLog?.Invoke(message);

        public void SetSimulationMode(bool enable)
        {
            SimulationMode = enable;
            Log(enable
                ? "🧠 Switched to Simulation Mode (fake random data)"
                : "🔌 Switched to Live Mode (real OBD-II readings)");
        }

        #region Bluetooth Connection

        public async Task<bool> AutoConnectAsync()
        {
            try
            {
                IsConnecting = true;
                IsConnected = false;
                Log("🔍 Scanning for devices...");
                _adapter.ScanTimeout = 10000;

                IDevice? veepeakDevice = null;
                _adapter.DeviceDiscovered += (s, a) =>
                {
                    if (!string.IsNullOrEmpty(a.Device.Name) &&
                        (a.Device.Name.Contains("VEEPEAK", StringComparison.OrdinalIgnoreCase) ||
                         a.Device.Name.Contains("OBD", StringComparison.OrdinalIgnoreCase)))
                    {
                        veepeakDevice = a.Device;
                    }
                };

                await _adapter.StartScanningForDevicesAsync();

                if (veepeakDevice == null)
                {
                    Log("❌ Could not find VEEPEAK device. Make sure it’s powered and paired.");
                    return false;
                }

                Log($"🔗 Found device: {veepeakDevice.Name} ({veepeakDevice.Id}) — connecting...");
                await _adapter.ConnectToDeviceAsync(veepeakDevice);
                Log("✅ Connected to VEEPEAK successfully.");
                _device = veepeakDevice;

                var services = await veepeakDevice.GetServicesAsync();
                foreach (var service in services)
                {
                    if (service.Id.ToString().StartsWith("000018") || service.Id.ToString().StartsWith("00002a"))
                        continue;

                    var characteristics = await service.GetCharacteristicsAsync();
                    foreach (var c in characteristics)
                    {
                        Log($"Service {service.Id} → Char {c.Id} (Read={c.CanRead}, Write={c.CanWrite}, Update={c.CanUpdate})");

                        if (_writeCharacteristic == null && c.CanWrite)
                        {
                            _writeCharacteristic = c;
                            Log($"✏️ Selected TX characteristic: {c.Id}");
                        }

                        if (_notifyCharacteristic == null && c.CanUpdate)
                        {
                            _notifyCharacteristic = c;
                            Log($"📨 Selected RX characteristic: {c.Id}");
                            _notifyCharacteristic.ValueUpdated += NotifyCharacteristic_ValueUpdated;
                            await _notifyCharacteristic.StartUpdatesAsync();
                        }
                    }
                }

                if (_writeCharacteristic == null || _notifyCharacteristic == null)
                {
                    Log("⚠️ Could not find suitable TX or RX characteristic.");
                    return false;
                }

                IsConnected = true;
                SimulationMode = false;
                Log("✅ OBD-II connection established.");

                // Initialize adapter and read supported PIDs
                await InitializeElm327Async();
                await ReadSupportedPIDsAsync();

                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ Connection error: {ex.Message}");
                return false;
            }
            finally
            {
                IsConnecting = false;
            }
        }

        private void NotifyCharacteristic_ValueUpdated(object? sender, CharacteristicUpdatedEventArgs e)
        {
            string response = Encoding.ASCII.GetString(e.Characteristic.Value ?? Array.Empty<byte>()).Trim();
            if (!string.IsNullOrWhiteSpace(response))
                Log($"[ELM RX] {response}");
        }

        #endregion

        #region ELM327 Initialization & PID Support

        private async Task InitializeElm327Async()
        {
            if (_writeCharacteristic == null || _notifyCharacteristic == null) return;

            string[] initCommands = new[]
            {
                "ATZ",   // reset
                "ATE0",   // echo off
                "ATL0",   // linefeed off
                "ATS0",   // spaces off
                "ATH0",   // headers off
                "ATSP0"  // auto protocol
            };

            foreach (var cmd in initCommands)
            {
                await SendElmCommandAsync(cmd);
                await Task.Delay(500);
            }

            Log("⏳ Waiting for CAN bus to stabilize...");
            await Task.Delay(8000); // CAN bus stabilization
        }

        private async Task ReadSupportedPIDsAsync()
        {
            try
            {
                var response = await SendElmCommandWithResponseAsync("0100");
                if (!string.IsNullOrEmpty(response))
                {
                    var bytes = ParseResponseBytes(response);
                    for (int i = 0; i < 32; i++)
                    {
                        if ((bytes[i / 8] & 1 << 7 - i % 8) != 0)
                        {
                            var pidHex = (i + 1).ToString("X2");
                            _supportedPids.Add(pidHex);
                        }
                    }
                }
                Log($"ℹ️ Supported PIDs: {string.Join(", ", _supportedPids)}");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Failed to read supported PIDs: {ex.Message}");
            }
        }

        #endregion

        #region PID Reading

        public async Task<CarData> GetLatestDataAsync()
        {
            await _pidLock.WaitAsync();
            try
            {
                if (SimulationMode || !IsConnected || _writeCharacteristic == null)
                    return GenerateSimulatedData();

                foreach (var pid in _pidsToRead)
                {
                    if (_failedPids.Contains(pid)) continue;
                    string pidShort = pid.Substring(2);

                    if (!_supportedPids.Contains(pidShort))
                    {
                        Log($"⚠️ PID {pid} not supported by vehicle.");
                        _failedPids.Add(pid);
                        continue;
                    }

                    try
                    {
                        var value = await ReadPidWithRetriesAsync(pid);
                        _lastValues[pid] = value;

                        switch (pid)
                        {
                            case "010D": _latestData.Speed = float.Parse(value); break;
                            case "010C": _latestData.RPM = float.Parse(value); break;
                            case "0111": _latestData.ThrottlePercent = float.Parse(value); break;
                            case "012F": _latestData.FuelLevelPercent = float.Parse(value); break;
                        }
                    }
                    catch
                    {
                        Log($"⚠️ PID {pid} failed, using previous value.");
                        _failedPids.Add(pid);
                    }

                    await Task.Delay(300);
                }

                _latestData.LastUpdated = DateTime.Now;
                return _latestData;
            }
            finally
            {
                _pidLock.Release();
            }
        }

        private async Task<string> ReadPidWithRetriesAsync(string pid, int retries = 2)
        {
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                var response = await SendElmCommandWithResponseAsync(pid);

                if (string.IsNullOrEmpty(response) ||
                    response.Contains("NO DATA") ||
                    response.Contains("SEARCHING") ||
                    response.Contains("CAN ERROR"))
                {
                    Log($"⚠️ Attempt {attempt + 1} failed for PID {pid}: {response}");
                    await Task.Delay(300);
                    continue;
                }

                var parsed = ParsePidValue(pid, response);
                if (parsed != null) return parsed;
            }

            throw new Exception($"Failed to read PID {pid} after {retries + 1} attempts.");
        }

        private string? ParsePidValue(string pid, string response)
        {
            try
            {
                var bytes = ParseResponseBytes(response);
                return pid switch
                {
                    "010D" => bytes[0].ToString(),
                    "010C" => ((bytes[0] * 256 + bytes[1]) / 4).ToString(),
                    "0111" => (bytes[0] * 100 / 255).ToString(),
                    "012F" => (bytes[0] * 100 / 255).ToString(),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private byte[] ParseResponseBytes(string response)
        {
            var cleaned = response.Replace(" ", "").Replace(">", "").Trim();
            var bytes = new List<byte>();
            for (int i = 0; i < cleaned.Length; i += 2)
                bytes.Add(Convert.ToByte(cleaned.Substring(i, 2), 16));
            return bytes.ToArray();
        }

        #endregion

        #region Helper Methods

        private async Task<string> SendElmCommandWithResponseAsync(string cmd, int timeoutMs = 3000)
        {
            if (_writeCharacteristic == null || _notifyCharacteristic == null)
                return string.Empty;

            var tcs = new TaskCompletionSource<string>();
            EventHandler<CharacteristicUpdatedEventArgs>? handler = null;

            handler = (s, e) =>
            {
                string response = Encoding.ASCII.GetString(e.Characteristic.Value ?? Array.Empty<byte>()).Trim();
                if (response.EndsWith(">"))
                    tcs.TrySetResult(response);
            };

            _notifyCharacteristic.ValueUpdated += handler;

            Log($"[ELM TX] {cmd}");
            await _writeCharacteristic.WriteAsync(Encoding.ASCII.GetBytes(cmd + "\r"));

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            _notifyCharacteristic.ValueUpdated -= handler;

            return completedTask == tcs.Task ? tcs.Task.Result : string.Empty;
        }

        private async Task SendElmCommandAsync(string cmd, int timeoutMs = 3000)
        {
            await SendElmCommandWithResponseAsync(cmd, timeoutMs);
            await Task.Delay(200);
        }

        private CarData GenerateSimulatedData()
        {
            _latestData = new CarData
            {
                Speed = _rand.Next(0, 121),
                RPM = _rand.Next(800, 6001),
                ThrottlePercent = _rand.Next(0, 101),
                FuelLevelPercent = _rand.Next(10, 101),
                OilTempF = _rand.Next(180, 221),
                CoolantTempF = _rand.Next(160, 221),
                IntakeTempF = _rand.Next(60, 101),
                LastUpdated = DateTime.Now
            };
            return _latestData;
        }

        #endregion
    }
}