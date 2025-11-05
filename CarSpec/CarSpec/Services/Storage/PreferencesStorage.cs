using CarSpec.Interfaces;
using Microsoft.Maui.Storage;
using System.Text.Json;

namespace CarSpec.Services.Storage
{
    public class PreferencesStorage : IAppStorage
    {
        public Task<bool> HasAsync(string key)
            => Task.FromResult(Preferences.ContainsKey(key));

        public Task<T?> GetAsync<T>(string key)
        {
            var json = Preferences.Get(key, string.Empty);
            return Task.FromResult(string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json));
        }

        public Task SetAsync<T>(string key, T value)
        {
            var json = JsonSerializer.Serialize(value);
            Preferences.Set(key, json);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key)
        {
            Preferences.Remove(key);
            return Task.CompletedTask;
        }
    }
}
