using CarSpec.Utils;

namespace CarSpec.Services.Bluetooth
{
    /// <summary>
    /// Handles Bluetooth state and device management.
    /// </summary>
    public class BluetoothManager
    {
        private readonly BluetoothDeviceScanner _scanner = new();

        public event Action<string>? OnLog;

        public async Task<object?> FindDeviceAsync(string name1, string name2)
        {
            OnLog?.Invoke($"🔍 Scanning for {name1}/{name2}...");
            var device = await _scanner.FindDeviceAsync(name1, name2);
            if (device == null)
                OnLog?.Invoke("❌ No compatible Bluetooth device found.");
            return device;
        }
    }
}