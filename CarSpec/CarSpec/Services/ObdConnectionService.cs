using CarSpec.Models;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using System.Text;

namespace CarSpec.Services
{
    public class ObdConnectionService
    {
        private readonly IBluetoothLE _bluetooth;
        private readonly IAdapter _adapter;
        private static readonly Guid ObdServiceUuid = Guid.Parse("0000FFF0-0000-1000-8000-00805F9B34FB");
        private static readonly Guid WriteCharacteristicUuid = Guid.Parse("0000FFF2-0000-1000-8000-00805F9B34FB");
        private static readonly Guid ReadCharacteristicUuid = Guid.Parse("0000FFF1-0000-1000-8000-00805F9B34FB");
        private IDevice? _device;
        private ICharacteristic? _writeCharacteristic;
        private ICharacteristic? _readCharacteristic;

        public bool SimulationMode { get; private set; } = true;

        public bool IsLiveConnected => _device != null && _device.State == DeviceState.Connected;
        public bool IsConnected => SimulationMode || IsLiveConnected;

        public ObdConnectionService()
        {
            _bluetooth = CrossBluetoothLE.Current;
            _adapter = _bluetooth.Adapter;
        }

        public async Task<bool> ConnectAsync(string deviceName)
        {
            try
            {
                Console.WriteLine("[OBD] Attempting live connection...");

                if (_bluetooth.State != BluetoothState.On)
                {
                    Console.WriteLine("[OBD] Bluetooth off, staying simulated.");
                    return false;
                }

                var knownDevices = _adapter.DiscoveredDevices;
                _device = knownDevices.FirstOrDefault(d => d.Name?.Contains(deviceName, StringComparison.OrdinalIgnoreCase) == true)
                          ?? await ScanAndConnectAsync(deviceName);

                if (_device == null)
                {
                    Console.WriteLine("[OBD] Live device not found.");
                    return false;
                }

                Console.WriteLine($"[OBD] Connecting to {_device.Name}...");
                await _adapter.ConnectToDeviceAsync(_device);

                var service = await _device.GetServiceAsync(ObdServiceUuid);
                if (service == null) return false;

                _writeCharacteristic = await service.GetCharacteristicAsync(WriteCharacteristicUuid);
                _readCharacteristic = await service.GetCharacteristicAsync(ReadCharacteristicUuid);
                if (_writeCharacteristic == null || _readCharacteristic == null) return false;

                Console.WriteLine("[OBD] Live connection established.");
                SimulationMode = false;

                await SendCommandAsync("ATZ");
                await Task.Delay(200);
                await SendCommandAsync("ATE0");
                await SendCommandAsync("ATL0");
                await SendCommandAsync("ATS0");
                await SendCommandAsync("ATH0");
                await SendCommandAsync("ATSP0");

                return true;
            }
            catch
            {
                Console.WriteLine("[OBD] Failed to connect, staying simulated.");
                return false;
            }
        }

        private async Task<IDevice?> ScanAndConnectAsync(string deviceName)
        {
            var tcs = new TaskCompletionSource<IDevice?>();

            _adapter.DeviceDiscovered += (s, a) =>
            {
                if (a.Device.Name?.Contains(deviceName, StringComparison.OrdinalIgnoreCase) == true)
                    tcs.TrySetResult(a.Device);
            };

            await _adapter.StartScanningForDevicesAsync();
            var device = await Task.WhenAny(tcs.Task, Task.Delay(3000)) == tcs.Task ? tcs.Task.Result : null;
            await _adapter.StopScanningForDevicesAsync();

            return device;
        }

        public async Task<string?> SendCommandAsync(string command)
        {
            if (SimulationMode) return GenerateSimulatedResponse(command);

            if (_writeCharacteristic == null)
                throw new InvalidOperationException("Live device not connected.");

            var bytes = Encoding.ASCII.GetBytes(command + "\r");
            await _writeCharacteristic.WriteAsync(bytes);
            await Task.Delay(200);

            if (_readCharacteristic != null)
            {
                var (data, _) = await _readCharacteristic.ReadAsync();
                return Encoding.ASCII.GetString(data);
            }

            return null;
        }

        public async Task<CarData> GetLatestDataAsync()
        {
            if (!IsConnected) throw new InvalidOperationException("Not connected.");

            if (SimulationMode) return GenerateSimulatedCarData();

            var rpmRaw = await SendCommandAsync("010C");
            var speedRaw = await SendCommandAsync("010D");
            var coolantRaw = await SendCommandAsync("0105");
            var intakeRaw = await SendCommandAsync("010F");
            var fuelRaw = await SendCommandAsync("012F");

            return new CarData
            {
                RPM = ParseRPM(rpmRaw),
                Speed = ParseSpeed(speedRaw),
                CoolantTempF = ParseTemperature(coolantRaw),
                IntakeTempF = ParseTemperature(intakeRaw),
                FuelLevelPercent = ParseFuel(fuelRaw),
                LastUpdated = DateTime.Now
            };
        }

        private CarData GenerateSimulatedCarData()
        {
            var rand = new Random();
            return new CarData
            {
                RPM = rand.Next(700, 7000),
                Speed = rand.Next(0, 120),
                ThrottlePercent = rand.Next(0, 100),
                FuelLevelPercent = rand.Next(20, 100),
                OilTempF = rand.Next(180, 240),
                CoolantTempF = rand.Next(170, 220),
                IntakeTempF = rand.Next(60, 120),
                LastUpdated = DateTime.Now
            };
        }

        private string GenerateSimulatedResponse(string command) => command switch
        {
            "010C" => "41 0C 1A F8",
            "010D" => "41 0D 28",
            "0105" => "41 05 7B",
            "010F" => "41 0F 50",
            "012F" => "41 2F 64",
            _ => "NO DATA"
        };

        public async Task DisconnectAsync()
        {
            if (_device != null)
            {
                await _adapter.DisconnectDeviceAsync(_device);
                _device = null;
                _writeCharacteristic = null;
                _readCharacteristic = null;
            }
            SimulationMode = true;
            Console.WriteLine("[OBD] Disconnected, switched to simulation.");
        }

        private int ParseRPM(string? response)
        {
            var bytes = ExtractBytes(response);
            return bytes.Length >= 2 ? ((bytes[0] * 256) + bytes[1]) / 4 : 0;
        }
        private int ParseSpeed(string? response)
        {
            var bytes = ExtractBytes(response);
            return bytes.Length >= 1 ? bytes[0] : 0;
        }
        private int ParseTemperature(string? response)
        {
            var bytes = ExtractBytes(response);
            return bytes.Length >= 1 ? bytes[0] - 40 : 0;
        }
        private int ParseFuel(string? response)
        {
            var bytes = ExtractBytes(response);
            return bytes.Length >= 1 ? (bytes[0] * 100) / 255 : 0;
        }
        private byte[] ExtractBytes(string? response)
        {
            if (string.IsNullOrWhiteSpace(response)) return Array.Empty<byte>();
            try
            {
                var clean = new string(response.Where(c => "0123456789ABCDEFabcdef".Contains(c)).ToArray());
                return Enumerable.Range(0, clean.Length / 2)
                                 .Select(i => Convert.ToByte(clean.Substring(i * 2, 2), 16))
                                 .ToArray();
            }
            catch { return Array.Empty<byte>(); }
        }
    }
}