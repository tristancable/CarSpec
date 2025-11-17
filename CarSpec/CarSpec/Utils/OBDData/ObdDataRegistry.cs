using CarSpec.Utils.OBDData._00_1F;
using CarSpec.Utils.OBDData._20_3F;
using CarSpec.Utils.OBDData._40_5F;

namespace CarSpec.Utils.OBDData
{
    public sealed class ObdDataRegistry
    {
        private readonly Dictionary<string, Func<IObdData>> _map =
            new(StringComparer.OrdinalIgnoreCase);

        public static ObdDataRegistry Default { get; } = CreateDefault();

        private static ObdDataRegistry CreateDefault()
        {
            var r = new ObdDataRegistry();

            // --- Core PIDs you already had ---
            r.Register(() => new EngineRPM());                          // 010C
            r.Register(() => new VehicleSpeed());                       // 010D
            r.Register(() => new ThrottlePosition());                   // 0111
            r.Register(() => new FuelTankLevel());                      // 012F
            r.Register(() => new EngineCoolantTemperature());           // 0105
            r.Register(() => new IntakeAirTemperature());               // 010F
            r.Register(() => new EngineOilTemperature());               // 015C
            r.Register(() => new CalculatedEngineLoad());               // 0104
            r.Register(() => new FuelPressure());                       // 010A
            r.Register(() => new IntakeManifoldAbsolutePressure());     // 010B
            r.Register(() => new TimingAdvance());                      // 010E
            r.Register(() => new MAFAirFlowRate());                     // 0110
            r.Register(() => new DistanceTraveledWithMILOn());          // 0121
            r.Register(() => new FuelRailPressure());                   // 0122
            r.Register(() => new FuelRailGaugePressure());              // 0123
            r.Register(() => new CommandedEGR());                       // 012C
            r.Register(() => new EGRError());                           // 012D
            r.Register(() => new CommandedEvaporativePurge());          // 012E
            r.Register(() => new WarmUpsSinceCodesCleared());           // 0130
            r.Register(() => new DistanceTraveledSinceCodesCleared());  // 0131
            r.Register(() => new EvapSystemVaporPressure());            // 0132
            r.Register(() => new AbsoluteBarometricPressure());         // 0133

            r.Register(() => new PidsSupported01_20()); // 0100
            r.Register(() => new PidsSupported21_40()); // 0120
            r.Register(() => new PidsSupported41_60()); // 0140

            return r;
        }

        public void Register(Func<IObdData> factory)
        {
            var instance = factory();
            _map[instance.Pid] = factory; // overwrite-safe
        }

        public bool TryCreate(string pid, out IObdData data)
        {
            if (_map.TryGetValue(pid, out var f))
            {
                data = f();
                return true;
            }
            data = null!;
            return false;
        }
    }
}