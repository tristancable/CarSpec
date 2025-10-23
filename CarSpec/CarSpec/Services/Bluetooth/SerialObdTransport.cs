using CarSpec.Interfaces;
using CarSpec.Utils;
using System.IO.Ports;
using System.Text;

namespace CarSpec.Services.Obd
{
    /// <summary>
    /// Serial transport for ELM327-style OBD-II adapters on Windows (COM port).
    /// </summary>
    public class SerialObdTransport : IObdTransport
    {
        private readonly Logger _log = new("SERIAL");
        private SerialPort? _port;

        // Adjust these for the adapter’s default serial settings.
        private const int BaudRate = 9600;
        private const int ReadTimeout = 1500;
        private const int WriteTimeout = 1500;

        public async Task<bool> ConnectAsync()
        {
            try
            {
                var ports = SerialPort.GetPortNames();
                if (ports.Length == 0)
                {
                    _log.Error("❌ No COM ports detected. Is the adapter paired via Bluetooth?");
                    return false;
                }

                string portName = ports.FirstOrDefault(p => p.Contains("COM", StringComparison.OrdinalIgnoreCase)) ?? ports[0];
                _log.Info($"🔌 Attempting to open serial port {portName}...");

                _port = new SerialPort(portName, BaudRate)
                {
                    ReadTimeout = ReadTimeout,
                    WriteTimeout = WriteTimeout,
                    Encoding = Encoding.ASCII,
                    NewLine = "\r\n"
                };

                _port.Open();
                await Task.Delay(500);

                if (!_port.IsOpen)
                {
                    _log.Error("❌ Failed to open serial port.");
                    return false;
                }

                _log.Info($"✅ Connected to {_port.PortName} at {BaudRate} baud.");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"❌ Serial connection error: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> WriteAsync(string data)
        {
            if (_port == null || !_port.IsOpen)
            {
                _log.Warn("⚠️ Port not open — cannot write.");
                return false;
            }

            try
            {
                _port.Write(data + "\r\n");
                _log.LogDebug($"➡️ Sent: {data.Trim()}");
                await Task.Delay(100);
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"Write error: {ex.Message}");
                return false;
            }
        }

        public async Task<string> ReadAsync()
        {
            if (_port == null || !_port.IsOpen)
            {
                _log.Warn("⚠️ Port not open — cannot read.");
                return string.Empty;
            }

            try
            {
                string line = _port.ReadExisting().Trim();
                await Task.Delay(100);
                _log.LogDebug($"⬅️ Received: {line}");
                return line;
            }
            catch (TimeoutException)
            {
                _log.Warn("⚠️ Read timeout — no response.");
                return string.Empty;
            }
            catch (Exception ex)
            {
                _log.Error($"Read error: {ex.Message}");
                return string.Empty;
            }
        }

        public void Disconnect()
        {
            if (_port != null && _port.IsOpen)
            {
                _port.Close();
                _log.Info("🔌 Serial port closed.");
            }
        }
    }
}