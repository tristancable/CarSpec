using CarSpec.Interfaces;
using CarSpec.Utils;
using Microsoft.Extensions.Logging;

namespace CarSpec.Services.Bluetooth
{
    /// <summary>
    /// BLE transport layer for ELM327 communication.
    /// Replace the placeholders with your existing BLE connection code.
    /// </summary>
    public class BleObdTransport : IObdTransport
    {
        private readonly object _device;
        private readonly Logger _log = new("BLE");

        public BleObdTransport(object device)
        {
            _device = device;
        }

        public async Task<bool> ConnectAsync()
        {
            _log.Info("Connecting via BLE...");
            await Task.Delay(1000);
            return true;
        }

        public async Task<bool> WriteAsync(string data)
        {
            _log.LogDebug($"➡️ {data}");
            await Task.Delay(10);
            return true;
        }

        public async Task<string> ReadAsync()
        {
            await Task.Delay(20);
            return "OK";
        }

        public void Disconnect() => _log.Info("🔌 BLE disconnected");
    }
}