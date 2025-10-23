using CarSpec.Interfaces;
using CarSpec.Models;
using CarSpec.Services.Bluetooth;
using CarSpec.Utils;

namespace CarSpec.Services.Obd
{
    /// <summary>
    /// Handles low-level communication with the ELM327 adapter (sending AT/PID commands).
    /// </summary>
    public class Elm327Adapter
    {
        private readonly IObdTransport _transport;
        private readonly Logger _log = new("ELM327");

        public event Action<string>? OnLog;
        public bool IsConnected { get; private set; }

        public Elm327Adapter(object device)
        {
#if WINDOWS
            _transport = new SerialObdTransport();
#else
            _transport = new BleObdTransport(device);
#endif
        }


        public async Task<bool> ConnectAsync()
        {
            Log("🔌 Starting ELM327 connection...");

            try
            {
                Log("🔍 Initializing BLE transport connection...");
                Log($"🔍 Transport type: {_transport.GetType().Name}");
                IsConnected = await _transport.ConnectAsync();

                if (IsConnected)
                {
                    Log("✅ Connection established. Sending ELM327 initialization commands...");
                }
                else
                {
                    Log("❌ Transport connection failed — BLE handshake unsuccessful.");
                    return false;
                }

                string[] initCmds = { "ATZ", "ATE0", "ATL0", "ATS0", "ATH0", "ATSP0" };
                foreach (var cmd in initCmds)
                {
                    var resp = await SendCommandAsync(cmd);
                    if (resp.RawResponse == "NO_DATA" || string.IsNullOrWhiteSpace(resp.RawResponse))
                    {
                        Log($"⚠️ Command {cmd} returned no data. Adapter may not be initialized.");
                    }
                    else
                    {
                        Log($"✅ Command {cmd} OK → {resp.RawResponse}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ ELM327 connection exception: {ex.Message}");
                return false;
            }
        }

        public async Task<ObdResponse> SendCommandAsync(string command)
        {
            await _transport.WriteAsync(command + "\r");
            var response = await _transport.ReadAsync();

            Log($"Sent: {command}");
            Log($"Response: {response}");

            if (string.IsNullOrWhiteSpace(response) || response.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                Log("⚠️ No ECU data (engine likely off or ECU asleep)");
                return new ObdResponse { Command = command, RawResponse = "NO_DATA" };
            }

            return new ObdResponse { Command = command, RawResponse = response };
        }

        private void Log(string message)
        {
            _log.LogDebug(message);
            OnLog?.Invoke($"[ELM327] {message}");
        }

        public void Disconnect()
        {
            _transport.Disconnect();
            IsConnected = false;
            Log("Disconnected from ELM327.");
        }
    }
}