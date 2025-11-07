using CarSpec.Constants;
using CarSpec.Interfaces;
using CarSpec.Models;

namespace CarSpec.Services.Profiles
{
    public sealed class VehicleProfileService : IVehicleProfileService
    {
        private readonly IAppStorage _storage;
        private List<VehicleProfile> _garage = new();
        public VehicleProfile? Current { get; private set; }
        public IReadOnlyList<VehicleProfile> All => _garage;

        public VehicleProfileService(IAppStorage storage) => _storage = storage;

        public async Task LoadAsync()
        {
            _garage = await _storage.GetAsync<List<VehicleProfile>>(AppKeys.VehicleProfiles)
                      ?? new List<VehicleProfile>();

            // Migration: ensure each has an Id
            foreach (var v in _garage.Where(v => string.IsNullOrWhiteSpace(v.Id)))
                v.Id = Guid.NewGuid().ToString("N");

            Current ??= await _storage.GetAsync<VehicleProfile>(AppKeys.VehicleProfile);

            if (Current is null && _garage.Count > 0)
                Current = _garage[0];

            // Persist migration if any Ids were added
            await _storage.SetAsync(AppKeys.VehicleProfiles, _garage);
            if (Current != null)
                await _storage.SetAsync(AppKeys.VehicleProfile, Current);
        }

        public async Task<List<VehicleProfile>> GetAllAsync()
        {
            await LoadAsync();
            return _garage.ToList();
        }

        public async Task SetCurrentAsync(VehicleProfile profile)
        {
            await LoadAsync();

            var existing = _garage.FirstOrDefault(v => v.Id == profile.Id);
            if (existing is null) _garage.Add(profile);

            Current = profile;
            await _storage.SetAsync(AppKeys.VehicleProfile, Current);
            await _storage.SetAsync(AppKeys.VehicleProfiles, _garage);
        }

        // Legacy wrapper used by older pages (no await)
        public void Set(VehicleProfile profile) => _ = SetCurrentAsync(profile);

        public async Task AddAsync(VehicleProfile profile)
        {
            await LoadAsync();

            if (string.IsNullOrWhiteSpace(profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");

            _garage.Add(profile);
            await _storage.SetAsync(AppKeys.VehicleProfiles, _garage);

            if (Current is null)
                await SetCurrentAsync(profile);
        }

        public async Task RemoveAsync(string id)
        {
            await LoadAsync();

            var idx = _garage.FindIndex(v => v.Id == id);
            if (idx >= 0)
            {
                var removed = _garage[idx];
                _garage.RemoveAt(idx);

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
                   (string.IsNullOrWhiteSpace(engine) ||
                    string.Equals(v.Engine, engine, StringComparison.OrdinalIgnoreCase)));

        public async Task LearnFromFingerprintAsync(EcuFingerprint fp, string transport = "BLE")
        {
            if (Current is null) return;

            Current.PreferredTransport ??= transport;
            Current.ProtocolDetected = string.IsNullOrWhiteSpace(fp.Protocol) ? Current.ProtocolDetected : fp.Protocol;
            Current.VinLast = fp.Vin ?? Current.VinLast;
            Current.WmiLast = fp.Wmi ?? Current.WmiLast;
            Current.YearDetected = fp.Year ?? Current.YearDetected;
            Current.CalIds = fp.CalIds?.ToList() ?? Current.CalIds;
            Current.SupportedPidsCache = fp.SupportedPids?.ToList() ?? Current.SupportedPidsCache;
            Current.LastConnectedUtc = DateTime.UtcNow;

            // Write back into garage + current
            var list = await GetAllAsync();
            var i = list.FindIndex(v => v.Id == Current.Id);
            if (i >= 0) list[i] = Current; else list.Add(Current);

            await _storage.SetAsync(AppKeys.VehicleProfiles, list);
            await _storage.SetAsync(AppKeys.VehicleProfile, Current);
        }
    }
}