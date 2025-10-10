using Microsoft.JSInterop;
using CarSpec.Shared.Models;
using CarSpec.Shared.Services;

namespace CarSpec.Web.Client.Services
{
    public class WebObdService : IObdService
    {
        private readonly IJSRuntime _js;
        public bool IsConnected { get; private set; }

        public WebObdService(IJSRuntime js)
        {
            _js = js;
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                await _js.InvokeVoidAsync("obd.connect");
                IsConnected = true;
                return true;
            }
            catch
            {
                IsConnected = false;
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            await _js.InvokeVoidAsync("obd.disconnect");
            IsConnected = false;
        }

        public async Task<CarData?> GetLatestDataAsync()
        {
            var result = await _js.InvokeAsync<CarData>("obd.getData");
            return result;
        }
    }
}