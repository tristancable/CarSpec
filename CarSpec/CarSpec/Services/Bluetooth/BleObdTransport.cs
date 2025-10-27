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
        private ICharacteristic? _tx;
        private ICharacteristic? _rx;
        private readonly ConcurrentQueue<string> _responseQueue = new();

        public BleObdTransport(object device)
        {
            _device = device as IDevice
                ?? throw new ArgumentException("Device must be of type IDevice.", nameof(device));
        }

        public async Task<bool> ConnectAsync()
        {
            _log.Info($"🔗 Connecting to BLE device: {_device.Name ?? "Unknown"}");

            try
            {
                var service = await _device.GetServiceAsync(Guid.Parse("0000FFF0-0000-1000-8000-00805F9B34FB"));
                if (service == null)
                {
                    _log.Error("❌ FFF0 service not found on device.");
                    return false;
                }

                _tx = await service.GetCharacteristicAsync(Guid.Parse("0000FFF2-0000-1000-8000-00805F9B34FB"));
                _rx = await service.GetCharacteristicAsync(Guid.Parse("0000FFF1-0000-1000-8000-00805F9B34FB"));

                if (_tx == null || _rx == null)
                {
                    _log.Error("❌ Missing TX or RX characteristic on FFF0 service.");
                    return false;
                }

                _rx.ValueUpdated += (s, e) =>
                {
                    try
                    {
                        var bytes = e.Characteristic.Value;
                        if (bytes == null || bytes.Length == 0)
                            return;

                        string raw = Encoding.ASCII.GetString(bytes).Trim();
                        string hex = BitConverter.ToString(bytes).Replace("-", " ");
                        _log.LogDebug($"⬅️ [RAW BYTES] {hex}");
                        _log.LogDebug($"⬅️ [RAW TEXT] \"{raw}\"");

                        // Always enqueue raw so we don't lose banners like "ELM327 v2.2"
                        if (!string.IsNullOrWhiteSpace(raw))
                            _responseQueue.Enqueue(raw);

                        // Still enqueue cleaned text for PID data later
                        string cleaned = CleanResponse(raw);
                        if (!string.IsNullOrWhiteSpace(cleaned))
                            _responseQueue.Enqueue(cleaned);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[RX Handler Error] {ex.Message}");
                    }
                };

                await _rx.StartUpdatesAsync();
                _log.Info("📡 Notifications enabled for RX characteristic.");

                // Give the chip time to wake
                _log.Info("⏳ Waiting for ELM327 to initialize (2.5s)...");
                await Task.Delay(2500);

                await WriteAsync("ATZ");
                string resp = await ReadAsync();

                // Combine and preserve raw responses (skip CleanResponse filtering)
                if (string.IsNullOrWhiteSpace(resp))
                {
                    _log.Warn("⚠️ Connected but no immediate response from ELM327. Try restarting the adapter.");
                    return false;
                }

                // Accept both version banners and echoed ATZ replies
                if (resp.Contains("ELM327", StringComparison.OrdinalIgnoreCase) ||
                    resp.Contains("ATZ", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Info($"✅ ELM327 responded successfully: {resp}");
                    return true;
                }

                // Wait a bit and try one more read, in case banner arrives slightly delayed
                await Task.Delay(800);
                string followUp = await ReadAsync();
                if (!string.IsNullOrWhiteSpace(followUp) &&
                    followUp.Contains("ELM327", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Info($"✅ ELM327 chip responded after delay: {followUp}");
                    return true;
                }

                _log.Warn($"⚠️ Unexpected ELM327 handshake response: {resp}");
                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"Connection error: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> WriteAsync(string data)
        {
            if (_tx == null)
            {
                _log.Warn("⚠️ TX characteristic not initialized.");
                return false;
            }

            try
            {
                var bytes = Encoding.ASCII.GetBytes(data + "\r");
                await _tx.WriteAsync(bytes);
                _log.LogDebug($"➡️ Sent: {data.Trim()}");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"[Write Error] {ex.Message}");
                return false;
            }
        }

        public async Task<string> ReadAsync()
        {
            for (int i = 0; i < 50; i++)
            {
                if (_responseQueue.TryDequeue(out var response))
                    return response;

                await Task.Delay(50);
            }

            _log.Warn("⚠️ No ECU data received in time.");
            return string.Empty;
        }

        public void Disconnect()
        {
            try
            {
                _log.Info("🔌 Disconnecting BLE...");

                if (_rx != null)
                {
                    try { _rx.StopUpdatesAsync().Wait(500); } catch { }
                    _rx.ValueUpdated -= null;
                    _rx = null;
                }

                _tx = null;

                if (_device != null)
                {
                    try
                    {
                        var bleDevice = _device;
                        bleDevice.Dispose();
                        _log.Info($"🧹 BLE device {_device.Name} disposed.");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"⚠️ Failed to fully dispose BLE device: {ex.Message}");
                    }
                }

                _responseQueue.Clear();
                _log.Info("✅ BLE transport disconnected and cleaned up.");
            }
            catch (Exception ex)
            {
                _log.Error($"[Disconnect Error] {ex.Message}");
            }
        }

        private static string CleanResponse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            string[] junk = { "SEARCHING", "?" };
            foreach (var token in junk)
            {
                if (input.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return string.Empty;
            }

            return input.Trim();
        }
    }
}