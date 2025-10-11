using CarSpec.Models;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace CarSpec.Services
{
    public class ObdConnectionService
    {
        private readonly IBluetoothLE _ble;
        private readonly IAdapter _adapter;
        private IDevice? _device;
        private ICharacteristic? _characteristic;
        private readonly Random _rand = new();

        public bool IsConnected { get; private set; }
        public bool IsLiveConnected => IsConnected && !SimulationMode;
        public bool SimulationMode { get; set; } = true; // start in simulation mode

        private CarData _latestData = new();

        public event Action<string>? OnLogUpdate; // for dashboard log updates

        public ObdConnectionService()
        {
            _ble = CrossBluetoothLE.Current;
            _adapter = CrossBluetoothLE.Current.Adapter;
        }

        private void Log(string message)
        {
            Console.WriteLine("[OBD] " + message);
            OnLogUpdate?.Invoke(message);
        }

        /// <summary>
        /// Tries to connect automatically to a known VEEPEAK BLE adapter.
        /// </summary>
        public async Task<bool> AutoConnectAsync()
        {
            try
            {
                Log("🔍 Scanning for Bluetooth devices...");
                Log("🔍 Scanning for nearby Bluetooth devices...");
                var device = await ScanForDeviceAsync("VEEPEAK");

                if (device == null)
                {
                    Log("⚠️ Could not find VEEPEAK adapter nearby. Make sure it's powered on and paired.");
                    SimulationMode = true;
                    return false;
                }

                await _adapter.ConnectToDeviceAsync(device);
                _device = device;
                _characteristic = await GetObdCharacteristicAsync(device);
                IsConnected = true;
                SimulationMode = false;
                Log("✅ Connected to Live OBD-II successfully!");
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ Connection failed: {ex.Message}");
                SimulationMode = true;
                return false;
            }
        }

        private async Task<IDevice?> ScanForDeviceAsync(string nameFragment)
        {
            TaskCompletionSource<IDevice?> tcs = new();
            EventHandler<DeviceEventArgs>? handler = null;

            handler = (s, e) =>
            {
                if (e.Device.Name != null && e.Device.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
                {
                    _adapter.DeviceDiscovered -= handler;
                    _adapter.StopScanningForDevicesAsync();
                    tcs.TrySetResult(e.Device);
                }
            };

            _adapter.DeviceDiscovered += handler;
            await _adapter.StartScanningForDevicesAsync();

            var device = await Task.WhenAny(tcs.Task, Task.Delay(10000)) == tcs.Task ? tcs.Task.Result : null;
            await _adapter.StopScanningForDevicesAsync();
            return device;
        }

        private async Task<ICharacteristic?> GetObdCharacteristicAsync(IDevice device)
        {
            var services = await device.GetServicesAsync();
            foreach (var service in services)
            {
                var characteristics = await service.GetCharacteristicsAsync();
                foreach (var c in characteristics)
                {
                    if (c.CanWrite && c.CanRead)
                        return c;
                }
            }
            return null;
        }

        /// <summary>
        /// Gets the latest car data, either simulated or live.
        /// </summary>
        public async Task<CarData> GetLatestDataAsync()
        {
            if (SimulationMode || !IsConnected || _characteristic == null)
                return GenerateSimulatedData();

            try
            {
                // Example — in a full setup you’d send and receive AT commands here
                // This just fakes values until PID requests are implemented
                _latestData = new CarData
                {
                    Speed = _rand.Next(0, 120),
                    RPM = _rand.Next(700, 6000),
                    ThrottlePercent = _rand.Next(0, 100),
                    FuelLevelPercent = _rand.Next(20, 100),
                    OilTempF = _rand.Next(160, 240),
                    CoolantTempF = _rand.Next(170, 230),
                    IntakeTempF = _rand.Next(70, 120),
                    LastUpdated = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                Log($"⚠️ Data read failed: {ex.Message}");
                SimulationMode = true;
            }

            await Task.Delay(200);
            return _latestData;
        }

        private CarData GenerateSimulatedData()
        {
            _latestData = new CarData
            {
                Speed = _rand.Next(0, 120),
                RPM = _rand.Next(700, 6000),
                ThrottlePercent = _rand.Next(0, 100),
                FuelLevelPercent = _rand.Next(20, 100),
                OilTempF = _rand.Next(160, 240),
                CoolantTempF = _rand.Next(170, 230),
                IntakeTempF = _rand.Next(70, 120),
                LastUpdated = DateTime.Now
            };

            return _latestData;
        }

        public void Disconnect()
        {
            try
            {
                if (_device != null)
                {
                    _adapter.DisconnectDeviceAsync(_device);
                    Log("🔌 Disconnected from OBD-II adapter.");
                }

                IsConnected = false;
                SimulationMode = true;
            }
            catch (Exception ex)
            {
                Log($"⚠️ Disconnect error: {ex.Message}");
            }
        }
    }
}