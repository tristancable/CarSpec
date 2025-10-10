using System.IO.Ports;

namespace CarSpec.Services
{
    public class ObdConnectionService
    {
        private SerialPort? _serialPort;

        public bool IsConnected => _serialPort != null && _serialPort.IsOpen;

        public async Task<bool> ConnectAsync(string portName, int baudRate = 9600)
        {
            try
            {
                _serialPort = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = 2000,
                    WriteTimeout = 2000
                };
                _serialPort.Open();

                await SendCommandAsync("ATZ");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OBD] Connection failed: {ex.Message}");
                return false;
            }
        }

        public async Task<string?> SendCommandAsync(string command)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
                throw new InvalidOperationException("Not connected to OBD adapter.");

            return await Task.Run(() =>
            {
                _serialPort.WriteLine(command + "\r");
                Thread.Sleep(200);
                return _serialPort.ReadExisting();
            });
        }

        public void Disconnect()
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
                _serialPort.Dispose();
            }
        }
    }
}