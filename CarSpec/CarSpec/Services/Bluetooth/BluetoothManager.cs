using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions.Extensions;

namespace CarSpec.Services.Bluetooth
{
    /// <summary>
    /// Handles Bluetooth state and device management.
    /// </summary>
    public class BluetoothManager
    {
        private readonly IBluetoothLE _ble;
        private readonly IAdapter _adapter;

        public event Action<string>? OnLog;

        public bool IsOn => _ble.IsOn;

        public BluetoothManager()
        {
            _ble = CrossBluetoothLE.Current;
            _adapter = _ble.Adapter;
        }

        public async Task<IDevice?> FindDeviceAsync(string name1, string name2)
        {
            if (!IsOn)
            {
                OnLog?.Invoke("⚠️ Bluetooth is turned off.");
                return null;
            }

            var label = string.IsNullOrWhiteSpace(name2) ? name1 : $"{name1}/{name2}";
            OnLog?.Invoke($"🔍 Scanning for {label} devices...");

            IDevice? foundDevice = null;

            void Handler(object? s, DeviceEventArgs a)
            {
                var n = a.Device?.Name;
                if (string.IsNullOrWhiteSpace(n)) return;

                if (n.Contains(name1, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(name2) &&
                     n.Contains(name2, StringComparison.OrdinalIgnoreCase)))
                {
                    foundDevice = a.Device;
                    // stop scan ASAP once we’ve found a match
                    try { _adapter.StopScanningForDevicesAsync(); } catch { }
                }
            }

            _adapter.DeviceDiscovered += Handler;

            try
            {
                // give the scan a bounded time window
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _adapter.StartScanningForDevicesAsync(cts.Token);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ Bluetooth scan failed: {ex.Message}");
                _adapter.DeviceDiscovered -= Handler;
                return null;
            }

            _adapter.DeviceDiscovered -= Handler;

            if (foundDevice == null)
                OnLog?.Invoke("⚠️ No matching Bluetooth devices found.");

            return foundDevice;
        }
    }
}