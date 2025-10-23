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
            _adapter.DeviceDiscovered += (s, a) =>
            {
                if (a.Device != null && !string.IsNullOrEmpty(a.Device.Name))
                {
                    if (a.Device.Name.Contains(name1, StringComparison.OrdinalIgnoreCase) ||
                        a.Device.Name.Contains(name2, StringComparison.OrdinalIgnoreCase))
                    {
                        foundDevice = a.Device;
                    }
                }
            };

            try
            {
                await _adapter.StartScanningForDevicesAsync();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ Bluetooth scan failed: {ex.Message}");
                return null;
            }

            if (foundDevice == null)
                OnLog?.Invoke("⚠️ No matching Bluetooth devices found.");

            return foundDevice;
        }
    }
}