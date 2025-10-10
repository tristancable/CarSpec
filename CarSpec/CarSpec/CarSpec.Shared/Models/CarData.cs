namespace CarSpec.Shared.Models
{
    public class CarData
    {
        public double Speed { get; set; }
        public double RPM { get; set; }
        public double ThrottlePercent { get; set; }
        public double FuelLevelPercent { get; set; }
        public double OilTempF { get; set; }
        public double CoolantTempF { get; set; }
        public double IntakeTempF { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.Now;
    }
}

//public override string ToString()
//{
//    return $"Speed: {Speed} mph, RPM: {RPM}, Throttle: {ThrottlePercent}%, Fuel Level: {FuelLevelPercent}%, Oil Temp: {OilTempF}°F, Coolant Temp: {CoolantTempF}°F, Intake Temp: {IntakeTempF}°F, Last Updated: {LastUpdated}";
//}