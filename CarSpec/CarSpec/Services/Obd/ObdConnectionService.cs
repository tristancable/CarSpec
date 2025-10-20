using CarSpec.Models;
using CarSpec.Services.Bluetooth;
using CarSpec.Utils;
using Microsoft.Extensions.Logging;

namespace CarSpec.Services.Obd
{
    /// <summary>
    /// High-level orchestrator for connecting to ELM327 and reading data.
    /// </summary>
    public class ObdConnectionService
    {
        private readonly BluetoothManager _bluetooth;
        private Elm327Adapter? _adapter;
        private ObdService? _obdService;
        private readonly Logger _log = new("OBD-CONNECT");

        public bool SimulationMode { get; set; } = true;
        public bool IsConnected => _adapter?.IsConnected ?? false;
        public bool IsConnecting { get; private set; }

        public event Action<string>? OnLog;

        public ObdConnectionService()
        {
            _bluetooth = new BluetoothManager();
            _bluetooth.OnLog += Log;
        }

        public async Task<bool> AutoConnectAsync() => await ConnectAsync();

        public async Task<bool> ConnectAsync()
        {
            IsConnecting = true;
            try
            {
                var device = await _bluetooth.FindDeviceAsync("VEEPEAK", "OBD");
                if (device == null)
                {
                    Log("⚠️ No OBD device found.");
                    SimulationMode = true;
                    return false;
                }

                _adapter = new Elm327Adapter(device);
                _adapter.OnLog += Log;

                if (!await _adapter.ConnectAsync())
                {
                    Log("❌ Failed to connect to adapter.");
                    SimulationMode = true;
                    return false;
                }

                _obdService = new ObdService(_adapter);
                await _obdService.InitializeAsync();
                SimulationMode = false;

                Log("✅ OBD-II adapter connected successfully.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Error] Connection failed: {ex.Message}");
                SimulationMode = true;
                return false;
            }
            finally
            {
                IsConnecting = false;
            }
        }

        public async Task<CarData> GetLatestDataAsync()
        {
            if (SimulationMode || _obdService == null)
                return CarData.Simulated();

            return await _obdService.GetLatestDataAsync();
        }

        private void Log(string msg)
        {
            _log.Info(msg);
            OnLog?.Invoke(msg);
        }
    }
}