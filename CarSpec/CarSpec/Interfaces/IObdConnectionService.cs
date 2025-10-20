using CarSpec.Models;

namespace CarSpec.Interfaces
{
    public interface IObdConnectionService
    {
        bool IsConnected { get; }
        bool SimulationMode { get; }
        Task<bool> ConnectAsync();
        Task<CarData> GetLatestDataAsync();
    }
}