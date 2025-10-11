using CarSpec.Models;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading.Tasks;

namespace CarSpec.Services
{
    public class ObdConnectionService
    {
        private SerialPort? _port;
        private readonly Random _rand = new();

        public bool IsConnected { get; private set; }
        public bool IsLiveConnected => IsConnected && !SimulationMode;
        public bool SimulationMode { get; set; } = false;

        private CarData _latestData = new();

        /// <summary>
        /// Tries to connect automatically to a known OBD-II device (like Veepeak).
        /// </summary>
        public async Task<bool> AutoConnectAsync()
        {
            try
            {
                Console.WriteLine("[OBD] Searching for available COM ports...");
                string[] ports = SerialPort.GetPortNames();
                Console.WriteLine($"[OBD] Found ports: {string.Join(", ", ports)}");

                foreach (string port in ports)
                {
                    if (port.Contains("VEEPEAK", StringComparison.OrdinalIgnoreCase) ||
                        port.Contains("OBD", StringComparison.OrdinalIgnoreCase) ||
                        port.Contains("COM", StringComparison.OrdinalIgnoreCase))
                    {
                        if (await ConnectAsync(port))
                        {
                            Console.WriteLine($"[OBD] Auto-connected to {port}");
                            return true;
                        }
                    }
                }

                Console.WriteLine("[OBD] No suitable OBD-II adapter found — enabling simulation mode.");
                SimulationMode = true;
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OBD] AutoConnect failed: {ex.Message}");
                SimulationMode = true;
                return false;
            }
        }

        /// <summary>
        /// Connects to the given port and sets up the OBD connection.
        /// </summary>
        public async Task<bool> ConnectAsync(string portName)
        {
            try
            {
                SimulationMode = false;
                Console.WriteLine($"[OBD] Attempting connection on {portName}...");

                _port = new SerialPort(portName, 115200)
                {
                    ReadTimeout = 2000,
                    WriteTimeout = 2000
                };
                _port.Open();

                // Optional: ELM327 init commands
                await Task.Delay(500);
                _port.WriteLine("ATZ\r");
                await Task.Delay(500);
                _port.WriteLine("ATE0\r");
                await Task.Delay(500);
                _port.WriteLine("ATL0\r");

                IsConnected = true;
                Console.WriteLine("[OBD] Connection successful!");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OBD] Connection failed: {ex.Message}");
                SimulationMode = true;
                IsConnected = false;
                return false;
            }
        }

        /// <summary>
        /// Returns simulated or live data depending on the current mode.
        /// </summary>
        public async Task<CarData> GetLatestDataAsync()
        {
            if (SimulationMode)
            {
                return GenerateSimulatedData();
            }

            try
            {
                if (_port == null || !_port.IsOpen)
                {
                    SimulationMode = true;
                    return GenerateSimulatedData();
                }

                // Example live data (replace with real PID requests if implemented)
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
                Console.WriteLine($"[OBD] Error reading data: {ex.Message}");
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
                if (_port != null && _port.IsOpen)
                    _port.Close();

                IsConnected = false;
                Console.WriteLine("[OBD] Disconnected.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OBD] Disconnect error: {ex.Message}");
            }
        }
    }
}
