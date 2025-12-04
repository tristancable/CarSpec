using CarSpec.Models;

namespace CarSpec.Interfaces
{
    public interface IVehicleProfileService
    {
        VehicleProfile? Current { get; }
        IReadOnlyList<VehicleProfile> All { get; }
        event Action? CurrentChanged;

        Task LoadAsync();
        Task<List<VehicleProfile>> GetAllAsync();

        Task SetCurrentAsync(VehicleProfile profile);

        void Set(VehicleProfile profile);

        Task AddAsync(VehicleProfile profile);
        Task RemoveAsync(string id);

        VehicleProfile? Find(string year, string make, string model, string? engine = null);

        Task LearnFromFingerprintAsync(EcuFingerprint fp, string transport = "BLE");

        Task UpsertAsync(VehicleProfile profile);
    }
}