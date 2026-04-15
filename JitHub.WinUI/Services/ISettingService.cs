namespace JitHub.Services;

public interface ISettingService
{
    void Save<T>(string key, T value);

    T Get<T>(string key);
}
