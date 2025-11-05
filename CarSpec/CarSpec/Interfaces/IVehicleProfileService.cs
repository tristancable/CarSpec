using CarSpec.Models;

namespace CarSpec.Interfaces
{
    public interface IVehicleProfileService
    {
        VehicleProfile? Current { get; }
        IReadOnlyList<VehicleProfile> All { get; }

        Task LoadAsync();                   // loads vehicles.json (no-op if already loaded)
        void Set(VehicleProfile profile);   // legacy setter (keep if others call it)

        // New helpers used by fingerprint flow / UI
        Task<List<VehicleProfile>> GetAllAsync();         // enumerate vehicles.json
        Task SetCurrentAsync(VehicleProfile profile);     // persist & set active

        // Optional helper
        VehicleProfile? Find(string year, string make, string model, string? engine = null);
    }
}