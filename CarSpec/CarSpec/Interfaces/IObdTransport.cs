namespace CarSpec.Interfaces
{
    /// <summary>
    /// Interface for a low-level OBD-II transport (BLE, Serial, WiFi).
    /// </summary>
    public interface IObdTransport
    {
        Task<bool> ConnectAsync();
        Task<bool> WriteAsync(string data);
        Task<string> ReadAsync();
        void Disconnect();
    }
}