using CarSpec.Interfaces;
using CarSpec.Utils;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System;
using System.Collections.Concurrent;
using System.Text;

namespace CarSpec.Services.Bluetooth
{
    public class BleObdTransport : IObdTransport
    {
        private readonly Logger _log = new("BLE");
        private readonly IDevice _device;
        private ICharacteristic? _tx;
        private ICharacteristic? _rx;
        private EventHandler<CharacteristicUpdatedEventArgs>? _rxHandler;
        private readonly ConcurrentQueue<string> _responseQueue = new();
        private volatile bool _isConnected;

        public BleObdTransport(object device)
        {
            _device = device as IDevice
                ?? throw new ArgumentException("Device must be of type IDevice.", nameof(device));
        }

        // Implements IObdTransport.ConnectAsync()
        public async Task<bool> ConnectAsync()
        {
            _log.Info($"🔗 Using provided BLE device: {_device.Name ?? "Unknown"}");

            if (!await DiscoverChannelsAsync())
            {
                _log.Error("❌ Could not discover a valid RX/TX pair on the device.");
                return false;
            }

            // Wire notifications
            _rxHandler = (s, e) =>
            {
                try
                {
                    var data = e.Characteristic?.Value;
                    if (data == null || data.Length == 0) return;

                    var text = Encoding.ASCII.GetString(data);
                    var clean = CleanResponse(text);
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        _responseQueue.Enqueue(clean);
                        // Optional: _log.LogDebug($"⬅️ {clean}");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"⚠️ RX handler error: {ex.Message}");
                }
            };

            _rx!.ValueUpdated += _rxHandler;
            await _rx.StartUpdatesAsync();

            _log.Info("✅ BLE connected & notifications enabled.");
            _isConnected = true;
            return true;
        }

        /// <summary>
        /// Try common ELM327 layouts, then fall back to capability scan.
        /// </summary>
        private async Task<bool> DiscoverChannelsAsync()
        {
            // 1) Standard FFF0 (RX=FFF1 notify, TX=FFF2 write)
            var fff0 = await _device.GetServiceAsync(Guid.Parse("0000FFF0-0000-1000-8000-00805F9B34FB"));
            if (fff0 != null)
            {
                var tryRx = await fff0.GetCharacteristicAsync(Guid.Parse("0000FFF1-0000-1000-8000-00805F9B34FB"));
                var tryTx = await fff0.GetCharacteristicAsync(Guid.Parse("0000FFF2-0000-1000-8000-00805F9B34FB"));
                if (IsNotify(tryRx) && IsWritable(tryTx))
                {
                    _rx = tryRx; _tx = tryTx;
                    _log.Info("🔎 Using service 0xFFF0 (RX=FFF1 notify, TX=FFF2 write).");
                    return true;
                }
            }

            // 2) HM-10 style FFE0/FFE1 (same char often does both)
            var ffe0 = await _device.GetServiceAsync(Guid.Parse("0000FFE0-0000-1000-8000-00805F9B34FB"));
            if (ffe0 != null)
            {
                var ffe1 = await ffe0.GetCharacteristicAsync(Guid.Parse("0000FFE1-0000-1000-8000-00805F9B34FB"));
                if (IsNotify(ffe1) || IsWritable(ffe1))
                {
                    _rx = IsNotify(ffe1) ? ffe1 : null;
                    _tx = IsWritable(ffe1) ? ffe1 : null;
                    if (_rx != null && _tx != null)
                    {
                        _log.Info("🔎 Using service 0xFFE0 (FFE1 as RX notify + TX write).");
                        return true;
                    }
                }
            }

            // 3) Fallback: scan all services/characteristics and pick Notify + Write
            var services = await _device.GetServicesAsync();
            foreach (var svc in services)
            {
                var chars = await svc.GetCharacteristicsAsync();
                foreach (var ch in chars)
                {
                    if (_rx == null && IsNotify(ch)) _rx = ch;
                    if (_tx == null && IsWritable(ch)) _tx = ch;
                    if (_rx != null && _tx != null)
                    {
                        _log.Info($"🔎 Using fallback characteristics RX={_rx.Uuid}, TX={_tx.Uuid}.");
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IsNotify(ICharacteristic? c) =>
            c != null && (c.Properties.HasFlag(CharacteristicPropertyType.Notify) ||
                          c.Properties.HasFlag(CharacteristicPropertyType.Indicate));

        private static bool IsWritable(ICharacteristic? c) =>
            c != null && (c.Properties.HasFlag(CharacteristicPropertyType.Write) ||
                          c.Properties.HasFlag(CharacteristicPropertyType.WriteWithoutResponse));

        public async Task<bool> WriteAsync(string data)
        {
            if (_tx == null)
            {
                _log.Warn("⚠️ TX characteristic not initialized.");
                return false;
            }

            try
            {
                // DO NOT append CR here; Elm327Adapter sends command + "\r".
                var bytes = Encoding.ASCII.GetBytes(data);
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
            if (!_isConnected || _rx == null)
                return string.Empty;

            // Wait up to ~8s: 80 * 100ms (aligned with Elm327Adapter timeout)
            for (int i = 0; i < 80; i++)
            {
                if (!_isConnected) return string.Empty;

                if (_responseQueue.TryDequeue(out var response))
                    return response;

                await Task.Delay(100);
            }

            if (_isConnected)
                _log.Warn("⚠️ No ECU data received in time.");
            return string.Empty;
        }

        public void Disconnect()
        {
            try
            {
                _log.Info("🔌 Disconnecting BLE...");

                // Flip state first so any in-flight reads bail out quickly
                _isConnected = false;                      // add this

                // Unblock any waiting reader with a prompt sentinel
                _responseQueue.Enqueue(">");              // add this

                if (_rx != null)
                {
                    try { _rx.StopUpdatesAsync().Wait(500); } catch { }
                    if (_rxHandler != null)
                        _rx.ValueUpdated -= _rxHandler;
                    _rxHandler = null;
                    _rx = null;
                }

                _tx = null;

                if (_device != null)
                {
                    try
                    {
                        _device.Dispose();
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

            string[] junk = { "SEARCHING", "?", "ELM327", "OBDII" };
            foreach (var token in junk)
            {
                if (input.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return string.Empty;
            }

            return input.Trim();
        }
    }
}