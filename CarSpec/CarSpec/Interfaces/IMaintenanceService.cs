using CarSpec.Models;

namespace CarSpec.Interfaces;

public interface IMaintenanceService
{
    Task<IReadOnlyList<MaintenanceItem>> GetForVehicleAsync(string vehicleProfileId);

    Task SaveAsync(MaintenanceItem item);

    Task DeleteAsync(string id);
}