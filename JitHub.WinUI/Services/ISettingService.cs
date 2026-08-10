namespace JitHub.Services;

public interface ISettingService
{
    bool Contains(string key);

    void Save<T>(string key, T value);

    T Get<T>(string key);
}
