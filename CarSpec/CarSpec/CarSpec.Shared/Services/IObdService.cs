using CarSpec.Shared.Models;

namespace CarSpec.Shared.Services
{
    public interface IObdService
    {
        Task<bool> ConnectAsync();
        Task DisconnectAsync();
        Task<CarData?> GetLatestDataAsync();
        bool IsConnected { get; }
    }
}