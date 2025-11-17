using CarSpec.Models;

namespace CarSpec.Interfaces
{
    public interface IVehicleProfileService
    {
        VehicleProfile? Current { get; }
        IReadOnlyList<VehicleProfile> All { get; }

        Task LoadAsync();
        Task<List<VehicleProfile>> GetAllAsync();

        Task SetCurrentAsync(VehicleProfile profile);

        // Convenience (legacy) setter used by older pages
        void Set(VehicleProfile profile);

        // Garage operations
        Task AddAsync(VehicleProfile profile);
        Task RemoveAsync(string id);

        // Optional helper some code still uses
        VehicleProfile? Find(string year, string make, string model, string? engine = null);

        // Called after a successful ECU fingerprint to persist learned info
        Task LearnFromFingerprintAsync(EcuFingerprint fp, string transport = "BLE");

        Task UpsertAsync(VehicleProfile profile);
    }
}