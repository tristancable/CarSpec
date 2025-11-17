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

            if (string.IsNullOrWhiteSpace(profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");

            var idx = _garage.FindIndex(v => v.Id == profile.Id);
            if (idx >= 0) _garage[idx] = profile;
            else _garage.Add(profile);

            Current = profile;

            await _storage.SetAsync(AppKeys.VehicleProfiles, _garage);
            await _storage.SetAsync(AppKeys.VehicleProfile, Current);
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
            await LoadAsync();
            if (Current is null) return;

            // Normalize protocol (drop "AUTO, ")
            static string? Normalize(string? p)
            {
                if (string.IsNullOrWhiteSpace(p)) return null;
                var s = p.Trim();
                if (s.StartsWith("AUTO", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) return parts[1];
                    return null;
                }
                return s;
            }

            bool changed = false;

            // Never overwrite good data with null/empty; only fill gaps
            if (string.IsNullOrWhiteSpace(Current.PreferredTransport) && !string.IsNullOrWhiteSpace(transport))
            { Current.PreferredTransport = transport; changed = true; }

            var protoNorm = Normalize(fp.Protocol);
            if (!string.IsNullOrWhiteSpace(protoNorm) &&
                !string.Equals(Current.ProtocolDetected ?? "", protoNorm, StringComparison.OrdinalIgnoreCase))
            { Current.ProtocolDetected = protoNorm; changed = true; }

            if (string.IsNullOrWhiteSpace(Current.VinLast) && !string.IsNullOrWhiteSpace(fp.Vin))
            { Current.VinLast = fp.Vin; Current.LastKnownVin = fp.Vin; changed = true; }

            if (string.IsNullOrWhiteSpace(Current.WmiLast) && !string.IsNullOrWhiteSpace(fp.Wmi))
            { Current.WmiLast = fp.Wmi; changed = true; }

            if (!Current.YearDetected.HasValue && fp.Year.HasValue)
            { Current.YearDetected = fp.Year; changed = true; }

            if (fp.CalIds is { Count: > 0 })
            {
                var newCal = fp.CalIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!(Current.CalIds ??= new List<string>()).SequenceEqual(newCal, StringComparer.OrdinalIgnoreCase))
                { Current.CalIds = newCal; changed = true; }
            }

            if (fp.SupportedPids is { Count: > 0 })
            {
                var newPids = fp.SupportedPids
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim().ToUpperInvariant())
                    .Distinct()
                    .OrderBy(s => s)
                    .ToList();

                var oldPids = (Current.SupportedPidsCache ?? new List<string>())
                    .Select(s => s.Trim().ToUpperInvariant())
                    .Distinct()
                    .OrderBy(s => s)
                    .ToList();

                if (!newPids.SequenceEqual(oldPids, StringComparer.Ordinal))
                { Current.SupportedPidsCache = newPids; changed = true; }
            }

            if (changed)
            {
                Current.LastConnectedUtc = DateTime.UtcNow;

                // single persistence path
                await UpsertAsync(Current);
                await SetCurrentAsync(Current);
            }
        }

        public async Task UpsertAsync(VehicleProfile profile)
        {
            await LoadAsync();

            if (string.IsNullOrWhiteSpace(profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");

            // Work on a mutable copy so we can replace/add cleanly
            var list = _garage.ToList();

            // Prefer Id match; fallback to Year|Make|Model for older rows without Id
            int idx = list.FindIndex(p =>
                (!string.IsNullOrWhiteSpace(p.Id) && p.Id == profile.Id) ||
                (p.Year == profile.Year && p.Make == profile.Make && p.Model == profile.Model));

            if (idx >= 0) list[idx] = profile;
            else list.Add(profile);

            // Persist to storage
            await _storage.SetAsync(AppKeys.VehicleProfiles, list);

            // Refresh in-memory cache and Current pointer
            _garage = list;
            if (Current?.Id == profile.Id)
                Current = profile;

            await _storage.SetAsync(AppKeys.VehicleProfile, Current);
        }
    }
}