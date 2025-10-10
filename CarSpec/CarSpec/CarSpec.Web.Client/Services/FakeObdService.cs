using CarSpec.Shared.Models;

namespace CarSpec.Shared.Services
{
    public class FakeObdService : IObdService
    {
        private readonly Random _rand = new();

        public bool IsConnected => throw new NotImplementedException();

        public Task<bool> ConnectAsync()
        {
            return Task.FromResult(true);
        }

        public Task DisconnectAsync()
        {
            throw new NotImplementedException();
        }

        public Task<CarData?> GetLatestDataAsync()
        {
            return Task.FromResult<CarData?>(new CarData
            {
                Speed = 0,
                RPM = 750,
                ThrottlePercent = 2.5,
                FuelLevelPercent = 55.0,
                OilTempF = 200,
                CoolantTempF = 185,
                IntakeTempF = 72
            });
        }
    }
}