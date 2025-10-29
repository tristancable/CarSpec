using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

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

            OnLog?.Invoke($"🔍 Scanning for {name1}/{name2} devices...");

            IDevice? foundDevice = null;

            // Capture handler so we can unsubscribe
            void Handler(object? s, DeviceEventArgs a)
            {
                var d = a.Device;
                if (d != null && !string.IsNullOrEmpty(d.Name))
                {
                    if (d.Name.Contains(name1, StringComparison.OrdinalIgnoreCase) ||
                        d.Name.Contains(name2, StringComparison.OrdinalIgnoreCase))
                    {
                        foundDevice = d;
                        // Try to stop early if supported
                        try { _adapter.StopScanningForDevicesAsync(); } catch { /* ignore */ }
                    }
                }
            }

            _adapter.DeviceDiscovered += Handler;

            try
            {
                await _adapter.StartScanningForDevicesAsync(); // returns when scan stops
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