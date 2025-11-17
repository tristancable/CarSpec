namespace CarSpec.Models
{
    /// <summary>
    /// Represents the latest OBD-II live data snapshot from the vehicle
    /// </summary>
    public class CarData
    {
        public double Speed { get; set; }
        public double RPM { get; set; }
        public double ThrottlePercent { get; set; }
        public double FuelLevelPercent { get; set; }
        public double OilTempF { get; set; }
        public double CoolantTempF { get; set; }
        public double IntakeTempF { get; set; }
        public double? EngineLoadPercent { get; set; }
        public double? MapKpa { get; set; }
        public double? MapPsi { get; set; }
        public double? FuelPressureKpa { get; set; }
        public double? FuelPressurePsi { get; set; }
        public double? TimingAdvanceDeg { get; set; }
        public double? MafGramsPerSec { get; set; }
        public int DistanceWithMilKm { get; set; }
        public int DistanceSinceClearKm { get; set; }
        public int WarmUpsSinceClear { get; set; }
        public double FuelRailPressureRelKPa { get; set; }
        public int FuelRailGaugePressureKPa { get; set; }
        public double CommandedEgrPercent { get; set; }
        public double EgrErrorPercent { get; set; }
        public double CommandedEvapPurgePercent { get; set; }
        public int EvapVaporPressurePa { get; set; }
        public int BaroKPa { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.Now;

        public static CarData Simulated() => new CarData
        {
            Speed = new Random().Next(0, 121),
            RPM = new Random().Next(800, 6001),
            ThrottlePercent = new Random().Next(0, 101),
            FuelLevelPercent = new Random().Next(10, 101),
            OilTempF = new Random().Next(180, 221),
            CoolantTempF = new Random().Next(160, 221),
            IntakeTempF = new Random().Next(60, 101),
            LastUpdated = DateTime.Now
        };
    }
}