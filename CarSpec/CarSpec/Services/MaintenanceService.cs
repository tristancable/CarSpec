using CarSpec.Interfaces;
using CarSpec.Models;

namespace CarSpec.Services
{
    public class MaintenanceService : IMaintenanceService
    {
        private readonly IAppStorage _storage;

        // Single bucket for all items, filter by VehicleProfileId in API
        private const string StorageKey = "maintenance_items_v1";

        public MaintenanceService(IAppStorage storage)
        {
            _storage = storage;
        }

        // ---- Helpers ----

        private async Task<List<MaintenanceItem>> LoadAllAsync()
        {
            var list = await _storage.GetAsync<List<MaintenanceItem>>(StorageKey);
            return list ?? new List<MaintenanceItem>();
        }

        private Task SaveAllAsync(List<MaintenanceItem> items)
        {
            return _storage.SetAsync(StorageKey, items);
        }

        // ---- IMaintenanceService implementation ----

        public async Task<IReadOnlyList<MaintenanceItem>> GetForVehicleAsync(string vehicleProfileId)
        {
            var all = await LoadAllAsync();

            if (string.IsNullOrWhiteSpace(vehicleProfileId))
                return all;

            return all
                .Where(x => x.VehicleProfileId == vehicleProfileId)
                // optional: show upcoming / most-recent first
                .OrderBy(x => x.NextDueDate ?? DateTime.MaxValue)
                .ThenBy(x => x.NextDueOdometer ?? int.MaxValue)
                .ToList();
        }

        public async Task SaveAsync(MaintenanceItem item)
        {
            if (item is null)
                throw new ArgumentNullException(nameof(item));

            if (string.IsNullOrWhiteSpace(item.VehicleProfileId))
                throw new ArgumentException("MaintenanceItem.VehicleProfileId is required.", nameof(item));

            // Ensure Id exists
            if (string.IsNullOrWhiteSpace(item.Id))
                item.Id = Guid.NewGuid().ToString("N");

            var all = await LoadAllAsync();

            var existingIndex = all.FindIndex(x => x.Id == item.Id);
            if (existingIndex >= 0)
            {
                all[existingIndex] = item;
            }
            else
            {
                all.Add(item);
            }

            await SaveAllAsync(all);
        }

        public async Task DeleteAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            var all = await LoadAllAsync();
            var removed = all.RemoveAll(x => x.Id == id);

            if (removed > 0)
            {
                await SaveAllAsync(all);
            }
        }
    }
}