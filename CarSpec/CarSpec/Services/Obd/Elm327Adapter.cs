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
            _transport = new BleObdTransport(device);
        }

        public async Task<bool> ConnectAsync()
        {
            IsConnected = await _transport.ConnectAsync();
            if (IsConnected)
                Log("Connection established.");
            else
                Log("Failed to connect.");

            return IsConnected;
        }

        public async Task<ObdResponse> SendCommandAsync(string command)
        {
            await _transport.WriteAsync(command + "\r");
            var response = await _transport.ReadAsync();

            Log($"Sent: {command}");
            Log($"Response: {response}");

            // Detect ECU not responding
            if (string.IsNullOrWhiteSpace(response) || response.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                Log("⚠️ No ECU data (engine likely off or ECU asleep)");
                return new ObdResponse { Command = command, RawResponse = "NO_DATA" };
            }

            return new ObdResponse { Command = command, RawResponse = response };
        }

        private void Log(string message)
        {
            _log.LogDebug(message); // local file/console logger
            OnLog?.Invoke($"[ELM327] {message}"); // event broadcast
        }

        public void Disconnect()
        {
            _transport.Disconnect();
            IsConnected = false;
            Log("Disconnected from ELM327.");
        }
    }
}