using CarSpec.Interfaces;
using CarSpec.Models;
using CarSpec.Services.Bluetooth;
using CarSpec.Utils;
using System.Text;

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
        public bool IsEcuAwake { get; private set; }

        public Elm327Adapter(object device)
        {
            _transport = new BleObdTransport(device);
        }

        public async Task<bool> ConnectAsync()
        {
            Log("🔌 Starting ELM327 connection...");

            try
            {
                Log("🔍 Initializing BLE transport connection...");
                Log($"🔍 Transport type: {_transport.GetType().Name}");
                IsConnected = await _transport.ConnectAsync();

                if (!IsConnected)
                {
                    Log("❌ Transport connection failed — BLE handshake unsuccessful.");
                    return false;
                }

                Log("✅ Connection established. Sending ELM327 initialization commands...");

                // --- Base Initialization Commands ---
                string[] baseCmds =
                {
                    "ATZ",     // Reset
                    "ATE0",    // Echo off
                    "ATL0",    // Linefeeds off
                    "ATS0"     // Spaces off
                };

                foreach (var cmd in baseCmds)
                {
                    var resp = await SendCommandAsync(cmd);
                    await Task.Delay(300);
                    Log(!string.IsNullOrWhiteSpace(resp.RawResponse)
                        ? $"✅ Command {cmd} OK → {resp.RawResponse}"
                        : $"⚠️ Command {cmd} returned no data.");
                }

                // --- Try Multiple Protocols ---
                bool protocolOK = false;
                string[] protocolSequence = { "ATSP0", "ATSPA", "ATSP3" };

                foreach (var proto in protocolSequence)
                {
                    Log($"🔧 Setting protocol: {proto}");
                    await SendCommandAsync(proto);
                    await Task.Delay(1000);

                    if (proto == "ATSP3")
                    {
                        Log("🕐 Attempting slow init for ISO9141-2...");
                        await SendCommandAsync("ATSI");   // initiate slow init
                        await Task.Delay(4000);           // give ECU 4 seconds to wake

                        // Adjust timeout and set header off again (Subaru ECUs need this)
                        await SendCommandAsync("ATST FF");
                        await SendCommandAsync("ATH0");
                        await Task.Delay(1000);

                        // Extra wake commands to fully open the K-line
                        await SendCommandAsync("ATD");    // reset connection state
                        await Task.Delay(1000);
                    }

                    var protoResp = await SendCommandAsync("ATDP");
                    if (!string.IsNullOrWhiteSpace(protoResp.RawResponse) &&
                        !protoResp.RawResponse.Contains("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"📡 Protocol detected: {protoResp.RawResponse}");
                        protocolOK = true;
                        break;
                    }
                }

                if (!protocolOK)
                {
                    Log("⚠️ Protocol auto-detect unsuccessful — forcing ISO 9141-2.");
                    await SendCommandAsync("ATSP3");
                    await Task.Delay(1000);
                }

                var finalProto = await SendCommandAsync("ATDP");
                Log($"📡 Active protocol: {finalProto.RawResponse}");
                await SendCommandAsync("ATH0");
                await Task.Delay(1000);

                // --- Verify ECU Communication ---
                await Task.Delay(2000);
                Log("🔎 Checking ECU communication...");

                // Subaru ECUs often need extra wake time
                Log("🕐 Performing extended 5-baud wake delay (4 seconds)...");
                await Task.Delay(4000);

                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    // Reset line before each attempt
                    await SendCommandAsync("ATD");
                    await Task.Delay(1000);

                    var ecuResp = await SendCommandAsync("0100");
                    await Task.Delay(2500);

                    var cleaned = ecuResp.RawResponse
                        .Replace("\r", "")
                        .Replace("\n", "")
                        .Replace(">", "")
                        .Replace(" ", "")
                        .ToUpperInvariant();

                    if (cleaned.Contains("4100"))
                    {
                        IsEcuAwake = true;
                        Log($"✅ ECU communication established on attempt {attempt}! → {cleaned}");
                        break;
                    }
                    else if (cleaned.Contains("BUSINIT") || cleaned.Contains("STOPPED"))
                    {
                        Log($"⚠️ Attempt {attempt}: ECU still initializing → {cleaned}");
                        await SendCommandAsync("ATSI"); // re-init if needed
                        await Task.Delay(4000);
                        continue;
                    }
                    else if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        Log($"⚠️ Attempt {attempt}: ECU not ready → {cleaned}");
                    }
                    else
                    {
                        Log($"⚠️ Attempt {attempt}: No response data received.");
                    }
                }

                if (!IsEcuAwake)
                {
                    Log("❌ ECU did not respond after extended attempts — switching to Simulation Mode.");
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
            var sb = new StringBuilder();
            var timeout = DateTime.Now.AddSeconds(6);

            while (DateTime.Now < timeout)
            {
                var chunk = await _transport.ReadAsync();
                if (!string.IsNullOrEmpty(chunk))
                {
                    sb.Append(chunk);
                    if (chunk.Contains('>')) break;
                }

                await Task.Delay(50);
            }

            string response = sb.ToString().Trim();

            // 🚧 Handle STOPPED gracefully
            if (response.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                Log($"⚠️ ECU returned STOPPED for {command} → waiting and retrying once...");
                await Task.Delay(800); // give ECU breathing room
                return await SendCommandAsync(command);
            }

            await Task.Delay(250); // ← pacing delay between each PID
            return new ObdResponse { Command = command, RawResponse = response };
        }

        private void Log(string message)
        {
            _log.LogDebug(message);
            OnLog?.Invoke($"[ELM327] {message}");
        }

        public void Disconnect()
        {
            try
            {
                _transport.Disconnect();
                IsConnected = false;
                Log("🔌 Disconnected from ELM327.");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Disconnect error: {ex.Message}");
            }
        }
    }
}