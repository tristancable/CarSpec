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

        // Legacy convenience (keep old callers working)
        void Set(VehicleProfile profile);

        // NEW: used by Garage.razor
        Task AddAsync(VehicleProfile profile);
        Task RemoveAsync(string id);

        // Learning from ECU
        Task LearnFromFingerprintAsync(EcuFingerprint fp);

        VehicleProfile? Find(string year, string make, string model, string? engine = null);
    }
}