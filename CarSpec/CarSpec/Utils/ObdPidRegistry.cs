using CarSpec.Utils.OBDData;

namespace CarSpec.Utils
{
    public static class ObdPidRegistry
    {
        public static AbstractObdData Create(string pid) => pid switch
        {
            "010C" => new EngineRPM(),
            "010D" => new VehicleSpeed(),
            "0111" => new ThrottlePosition(),
            "012F" => new FuelTankLevel(),
            "0105" => new EngineCoolantTemperature(),
            "010F" => new IntakeAirTemperature(),
            "015C" => new EngineOilTemperature(),
            _ => throw new NotSupportedException($"PID {pid} not registered.")
        };

        public static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            "010C","010D","0111","012F","0105","010F","015C"
        };
    }
}