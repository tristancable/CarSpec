namespace CarSpec.Interfaces
{
    public interface IAppStorage
    {
        Task<bool> HasAsync(string key);
        Task<T?> GetAsync<T>(string key);
        Task SetAsync<T>(string key, T value);
        Task RemoveAsync(string key);
    }
}