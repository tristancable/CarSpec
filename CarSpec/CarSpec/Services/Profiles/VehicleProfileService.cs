using System.Text.Json;
using System.Text.Json.Serialization;
using CarSpec.Constants;
using CarSpec.Interfaces;
using CarSpec.Models;

namespace CarSpec.Services.Profiles
{
    public sealed class VehicleProfileService : IVehicleProfileService
    {
        private readonly IAppStorage _storage;
        private IReadOnlyList<VehicleProfile> _all = Array.Empty<VehicleProfile>();

        public VehicleProfile? Current { get; private set; }
        public IReadOnlyList<VehicleProfile> All => _all;

        public VehicleProfileService(IAppStorage storage) => _storage = storage;

        public async Task LoadAsync()
        {
            if (_all.Count == 0)
                _all = await LoadCatalogAsync();

            if (Current is null)
            {
                // 1) Try previously saved “current” profile
                var saved = await _storage.GetAsync<VehicleProfile>(AppKeys.VehicleProfile);
                if (saved is not null) { Current = saved; return; }

                // 2) Otherwise pick the first in user “garage” if any
                var garage = await _storage.GetAsync<List<VehicleProfile>>(AppKeys.VehicleProfiles);
                if (garage?.Count > 0) { Current = garage[0]; return; }

                // 3) Otherwise, first from catalog (if any)
                if (_all.Count > 0) Current = _all[0];
            }
        }

        public void Set(VehicleProfile profile) => Current = profile; // legacy

        // NEW: enumerate catalog
        public Task<List<VehicleProfile>> GetAllAsync()
        {
            // Ensure loaded; if caller didn't call LoadAsync(), that’s okay.
            if (_all.Count == 0)
            {
                return Task.Run(async () =>
                {
                    _all = await LoadCatalogAsync();
                    return _all.ToList();
                });
            }
            return Task.FromResult(_all.ToList());
        }

        // NEW: persist & set active
        public async Task SetCurrentAsync(VehicleProfile profile)
        {
            Current = profile;

            // Save the current selection
            await _storage.SetAsync(AppKeys.VehicleProfile, profile);

            // Maintain a “garage” list (MRU-ish, no duplicates by composite key)
            var garage = await _storage.GetAsync<List<VehicleProfile>>(AppKeys.VehicleProfiles)
                         ?? new List<VehicleProfile>();

            string Key(VehicleProfile v) => $"{v.Year}|{v.Make}|{v.Model}|{v.Engine}".ToUpperInvariant();
            var key = Key(profile);

            // remove any existing entry with same key
            garage.RemoveAll(v => Key(v) == key);
            // add to front
            garage.Insert(0, profile);

            // Optional: keep it small
            const int MaxGarage = 12;
            if (garage.Count > MaxGarage) garage = garage.Take(MaxGarage).ToList();

            await _storage.SetAsync(AppKeys.VehicleProfiles, garage);
        }

        public VehicleProfile? Find(string year, string make, string model, string? engine = null) =>
            _all.FirstOrDefault(v =>
                string.Equals(v.Year, year, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(v.Make, make, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(v.Model, model, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(engine) || string.Equals(v.Engine, engine, StringComparison.OrdinalIgnoreCase)));

        // ---------- catalog loader ----------

        private static async Task<IReadOnlyList<VehicleProfile>> LoadCatalogAsync()
        {
            // Adjust path to wherever you ship vehicles.json
            // Option A: next to your exe
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "vehicles.json"),
                Path.Combine(baseDir, "wwwroot", "vehicles.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "vehicles.json"),
            };

            var path = candidates.FirstOrDefault(File.Exists);
            if (path is null) return Array.Empty<VehicleProfile>();

            await using var fs = File.OpenRead(path);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var list = await JsonSerializer.DeserializeAsync<List<VehicleProfile>>(fs, opts)
                       ?? new List<VehicleProfile>();
            return list;
        }
    }
}