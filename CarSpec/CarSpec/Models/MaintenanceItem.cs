namespace CarSpec.Models;

public enum MaintenanceType
{
    OilChange,
    TireRotation,
    BrakeService,
    Inspection,
    FluidChange,
    FilterChange,
    Custom
}

public class MaintenanceItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // Link to your existing vehicle profile
    public string VehicleProfileId { get; set; } = default!;

    public MaintenanceType Type { get; set; } = MaintenanceType.Custom;

    /// <summary>Short title shown in the list (e.g. "Oil Change").</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional extra details (shop name, notes, etc.).</summary>
    public string? Notes { get; set; }

    // When it was last done
    public DateTime? LastDoneDate { get; set; }
    public int? LastDoneOdometer { get; set; }

    // When it should be done next
    public DateTime? NextDueDate { get; set; }
    public int? NextDueOdometer { get; set; }

    // Optional cost tracking
    public decimal? Cost { get; set; }

    /// <summary>True if we’ve already logged this occurrence into history.</summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// Convenience: distance remaining until due (positive = miles left, negative = overdue)
    /// </summary>
    public int? MilesRemaining(int? currentOdometer)
    {
        if (!NextDueOdometer.HasValue || !currentOdometer.HasValue) return null;
        return NextDueOdometer.Value - currentOdometer.Value;
    }
}