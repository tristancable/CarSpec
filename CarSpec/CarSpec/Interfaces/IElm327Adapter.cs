using CarSpec.Models;

namespace CarSpec.Interfaces
{
    public interface IElm327Adapter
    {
        bool IsConnected { get; }
        Task<bool> ConnectAsync();
        Task<ObdResponse> SendCommandAsync(string command);
        void Disconnect();
    }
}