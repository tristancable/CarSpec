// File: Utils/OBDData/ObdDataRegistry.cs
using System;
using System.Collections.Generic;
using CarSpec.Models;

// Bring in your concrete classes’ namespaces:
using CarSpec.Utils.OBDData;

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

            r.Register(() => new EngineRPM());                 // 010C
            r.Register(() => new VehicleSpeed());              // 010D
            r.Register(() => new ThrottlePosition());          // 0111
            r.Register(() => new FuelTankLevel());             // 012F
            r.Register(() => new EngineCoolantTemperature());  // 0105
            r.Register(() => new IntakeAirTemperature());      // 010F
            r.Register(() => new EngineOilTemperature());      // 015C (if you add it)

            return r;
        }

        public void Register(Func<IObdData> factory)
        {
            var instance = factory();
            _map[instance.Pid] = factory;
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