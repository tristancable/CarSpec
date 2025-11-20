using CarSpec.Interfaces;
using Microsoft.Maui.Storage;
using System.Text.Json;

namespace CarSpec.Services.Storage
{
    public sealed class FileStorage : IAppStorage
    {
        private readonly string _root;

        public FileStorage()
        {
            _root = Path.Combine(FileSystem.AppDataDirectory, "appdata");
            Directory.CreateDirectory(_root);
        }

        private static string Sanitize(string key)
        {
            // replace chars that aren't allowed in file names
            foreach (var c in Path.GetInvalidFileNameChars())
                key = key.Replace(c, '_');

            // also handle ':' from "rec.gz:<id>"
            return key.Replace(':', '_');
        }

        private string PathFor(string key)
            => Path.Combine(_root, Sanitize(key) + ".json");

        public Task<bool> HasAsync(string key)
            => Task.FromResult(File.Exists(PathFor(key)));

        public async Task<T?> GetAsync<T>(string key)
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return default;

            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<T>(json);
        }

        public async Task SetAsync<T>(string key, T value)
        {
            var path = PathFor(key);
            var json = JsonSerializer.Serialize(value);
            await File.WriteAllTextAsync(path, json);
        }

        public Task RemoveAsync(string key)
        {
            var path = PathFor(key);
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }
    }
}