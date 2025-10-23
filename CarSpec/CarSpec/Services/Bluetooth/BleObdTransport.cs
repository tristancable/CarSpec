using CarSpec.Interfaces;
using CarSpec.Utils;
using Plugin.BLE.Abstractions.Contracts;
using System.Text;
using System.Collections.Concurrent;

namespace CarSpec.Services.Bluetooth
{
    public class BleObdTransport : IObdTransport
    {
        private readonly Logger _log = new("BLE");
        private readonly IDevice _device;

        public BleObdTransport(object device)
        {
            _device = device as IDevice
                ?? throw new ArgumentException("Device must be of type IDevice.", nameof(device));
        }

        public async Task<bool> ConnectAsync()
        {
            _log.Info($"🔗 Connecting to BLE device: {_device.Name ?? "Unknown"}");
            _log.Info("🧭 Starting BLE service discovery...");
            System.Diagnostics.Debug.WriteLine("[BLE Transport] Begin service scan");
            Console.WriteLine("[BLE Transport] Begin service scan");

            try
            {
                // Log all services and characteristics
                var services = await _device.GetServicesAsync();
                _log.Info($"🔍 Found {services.Count} BLE services on device {_device.Name ?? "Unknown"}:");

                foreach (var service in services)
                {
                    _log.Info($"🧩 Service: {service.Id}");
                    var chars = await service.GetCharacteristicsAsync();

                    foreach (var c in chars)
                    {
                        string props = "";
                        if (c.CanRead) props += "Read ";
                        if (c.CanWrite) props += "Write ";
                        if (c.CanUpdate) props += "Notify ";
                        _log.Info($"   ↳ Characteristic: {c.Id} ({props.Trim()})");
                    }
                }

                _log.Warn("⚠️ Discovery complete — copy these UUIDs and send them to me so we can identify TX/RX.");
                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"Connection error: {ex.Message}");
                return false;
            }
        }

        public Task<bool> WriteAsync(string data)
        {
            _log.Warn("WriteAsync called during discovery mode — skipping actual write.");
            return Task.FromResult(false);
        }

        public Task<string> ReadAsync()
        {
            _log.Warn("ReadAsync called during discovery mode — skipping actual read.");
            return Task.FromResult(string.Empty);
        }

        public void Disconnect()
        {
            _log.Info("🔌 BLE disconnected (discovery mode).");
        }
    }
}


//using CarSpec.Interfaces;
//using CarSpec.Utils;
//using Plugin.BLE.Abstractions.Contracts;
//using System.Text;
//using System.Collections.Concurrent;

//namespace CarSpec.Services.Bluetooth
//{
//    public class BleObdTransport : IObdTransport
//    {
//        private readonly Logger _log = new("BLE");
//        private readonly IDevice _device;
//        private ICharacteristic? _tx;
//        private ICharacteristic? _rx;
//        private readonly ConcurrentQueue<string> _responseQueue = new();

//        public BleObdTransport(object device)
//        {
//            _device = device as IDevice
//                ?? throw new ArgumentException("Device must be of type IDevice.", nameof(device));
//        }

//        public async Task<bool> ConnectAsync()
//        {
//            _log.Info($"🔗 Connecting to BLE device: {_device.Name ?? "Unknown"}");

//            try
//            {
//                var service = await _device.GetServiceAsync(Guid.Parse("0000FFF0-0000-1000-8000-00805F9B34FB"));
//                if (service == null)
//                {
//                    _log.Error("❌ FFF0 service not found on device.");
//                    return false;
//                }

//                _tx = await service.GetCharacteristicAsync(Guid.Parse("0000FFF2-0000-1000-8000-00805F9B34FB"));
//                _rx = await service.GetCharacteristicAsync(Guid.Parse("0000FFF1-0000-1000-8000-00805F9B34FB"));

//                if (_tx == null || _rx == null)
//                {
//                    _log.Error("❌ Missing TX or RX characteristic on FFF0 service.");
//                    return false;
//                }

//                // Enable notifications for RX (incoming data)
//                _rx.ValueUpdated += (s, e) =>
//                {
//                    var bytes = e.Characteristic.Value;
//                    var raw = Encoding.ASCII.GetString(bytes).Trim();
//                    var cleaned = CleanResponse(raw);

//                    if (!string.IsNullOrWhiteSpace(cleaned))
//                    {
//                        _responseQueue.Enqueue(cleaned);
//                        _log.LogDebug($"⬅️ Received: {cleaned}");
//                    }
//                };

//                await _rx.StartUpdatesAsync();
//                _log.Info("📡 Notifications enabled for RX characteristic.");

//                // Test handshake
//                await Task.Delay(1200);
//                await WriteAsync("ATZ");
//                string resp = await ReadAsync();

//                if (string.IsNullOrWhiteSpace(resp))
//                {
//                    _log.Warn("⚠️ Connected but no ELM327 response. Try restarting the adapter.");
//                    return false;
//                }

//                _log.Info($"✅ ELM327 handshake successful: {resp}");
//                return true;
//            }
//            catch (Exception ex)
//            {
//                _log.Error($"Connection error: {ex.Message}");
//                return false;
//            }
//        }

//        public async Task<bool> WriteAsync(string data)
//        {
//            if (_tx == null)
//            {
//                _log.Warn("⚠️ TX characteristic not initialized.");
//                return false;
//            }

//            var bytes = Encoding.ASCII.GetBytes(data + "\r");
//            await _tx.WriteAsync(bytes);
//            _log.LogDebug($"➡️ Sent: {data.Trim()}");
//            return true;
//        }

//        public async Task<string> ReadAsync()
//        {
//            for (int i = 0; i < 30; i++)
//            {
//                if (_responseQueue.TryDequeue(out var response))
//                    return response;

//                await Task.Delay(50);
//            }

//            _log.Warn("⚠️ No ECU data received in time.");
//            return string.Empty;
//        }

//        public void Disconnect()
//        {
//            if (_rx != null)
//                _rx.StopUpdatesAsync();

//            _log.Info("🔌 BLE disconnected");
//        }

//        /// <summary>
//        /// Removes echoed commands and noise from raw ELM327 responses.
//        /// </summary>
//        private static string CleanResponse(string input)
//        {
//            if (string.IsNullOrWhiteSpace(input))
//                return string.Empty;

//            string[] junk = { "SEARCHING", "ELM327", ">", "?" };
//            foreach (var token in junk)
//            {
//                if (input.Contains(token, StringComparison.OrdinalIgnoreCase))
//                    return string.Empty;
//            }

//            // Remove repeated command echoes like "010C" in the response
//            if (input.Length <= 5 || input.All(char.IsLetterOrDigit))
//                return string.Empty;

//            return input.Trim();
//        }
//    }
//}