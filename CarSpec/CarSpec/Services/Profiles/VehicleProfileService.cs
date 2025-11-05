using CarSpec.Constants;
using CarSpec.Interfaces;
using CarSpec.Models;

namespace CarSpec.Services.Profiles
{
    public sealed class VehicleProfileService : IVehicleProfileService
    {
        private readonly IAppStorage _storage;
        private List<VehicleProfile> _garage = new();               // NEW: user garage
        private IReadOnlyList<VehicleProfile> _all = Array.Empty<VehicleProfile>();
        public VehicleProfile? Current { get; private set; }
        public IReadOnlyList<VehicleProfile> All => _garage;        // expose garage

        public VehicleProfileService(IAppStorage storage) => _storage = storage;

        public async Task LoadAsync()
        {
            // Load garage (user-created)
            _garage = await _storage.GetAsync<List<VehicleProfile>>(AppKeys.VehicleProfiles) ?? new List<VehicleProfile>();

            // Load current selection
            Current ??= await _storage.GetAsync<VehicleProfile>(AppKeys.VehicleProfile);

            // If nothing picked yet but garage has entries, pick first
            if (Current is null && _garage.Count > 0)
                Current = _garage[0];
        }

        public async Task<List<VehicleProfile>> GetAllAsync()
        {
            await LoadAsync();
            return _garage.ToList();
        }

        public async Task SetCurrentAsync(VehicleProfile profile)
        {
            await LoadAsync();

            // Ensure profile is in garage
            if (!_garage.Any(v => v.Id == profile.Id))
                _garage.Add(profile);

            Current = profile;
            await _storage.SetAsync(AppKeys.VehicleProfile, Current);
            await _storage.SetAsync(AppKeys.VehicleProfiles, _garage);
        }

        // Legacy wrapper
        public void Set(VehicleProfile profile) => _ = SetCurrentAsync(profile);

        public async Task AddAsync(VehicleProfile profile)          // NEW
        {
            await LoadAsync();

            if (string.IsNullOrWhiteSpace(profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");

            _garage.Add(profile);
            await _storage.SetAsync(AppKeys.VehicleProfiles, _garage);

            // If this is the first car, also set as Current for convenience
            if (Current is null)
                await SetCurrentAsync(profile);
        }

        public async Task RemoveAsync(string id)                    // NEW
        {
            await LoadAsync();

            var idx = _garage.FindIndex(v => v.Id == id);
            if (idx >= 0)
            {
                var removed = _garage[idx];
                _garage.RemoveAt(idx);

                // If we removed the current one, pick another if available
                if (Current?.Id == removed.Id)
                    Current = _garage.FirstOrDefault();

                await _storage.SetAsync(AppKeys.VehicleProfiles, _garage);
                await _storage.SetAsync(AppKeys.VehicleProfile, Current);
            }
        }

        public VehicleProfile? Find(string year, string make, string model, string? engine = null)
            => _garage.FirstOrDefault(v =>
                string.Equals(v.Year, year, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(v.Make, make, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(v.Model, model, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(engine) || string.Equals(v.Engine, engine, StringComparison.OrdinalIgnoreCase)));

        public async Task LearnFromFingerprintAsync(EcuFingerprint fp)
        {
            await LoadAsync();
            if (Current is null) return;

            if (!string.IsNullOrWhiteSpace(fp.Protocol))
                Current.ProtocolDetected = fp.Protocol;

            if (!string.IsNullOrWhiteSpace(fp.Vin))
                Current.VinCached = fp.Vin;

            if (fp.SupportedPids.Count > 0)
                Current.SupportedPidsCache = fp.SupportedPids.ToList();

            await _storage.SetAsync(AppKeys.VehicleProfile, Current);

            // Also update the profile inside the garage list
            var i = _garage.FindIndex(v => v.Id == Current.Id);
            if (i >= 0) _garage[i] = Current;
            await _storage.SetAsync(AppKeys.VehicleProfiles, _garage);
        }
    }
}