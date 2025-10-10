namespace CarSpec.Services
{
    public class ObdService
    {
        public Task<string> ConnectAsync()
        {
            // TODO: Implement Bluetooth or Wi-Fi ELM327 connection
            return Task.FromResult("Connected to OBD-II adapter.");
        }

        public Task<int> GetRpmAsync()
        {
            // TODO: Query PID 0C for RPM
            return Task.FromResult(750);
        }

        public Task<int> GetSpeedAsync()
        {
            // TODO: Query PID 0D for Speed
            return Task.FromResult(0);
        }
    }
}