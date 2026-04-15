using System;
using Windows.Storage;

namespace JitHub.Services;

public class SettingService : ISettingService
{
    private readonly ApplicationDataContainer _store = ApplicationData.Current.LocalSettings;

    public T Get<T>(string key)
    {
        if (!_store.Values.TryGetValue(key, out object? value) || value is null)
        {
            return default!;
        }

        if (value is T typedValue)
        {
            return typedValue;
        }

        Type targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (value is string stringValue)
        {
            if (targetType == typeof(string))
            {
                return (T)(object)stringValue;
            }

            if (targetType.IsEnum)
            {
                return (T)Enum.Parse(targetType, stringValue);
            }
        }

        if (targetType.IsEnum)
        {
            return (T)Enum.ToObject(targetType, value);
        }

        if (value is IConvertible)
        {
            return (T)Convert.ChangeType(value, targetType);
        }

        throw new NotSupportedException($"Setting type '{targetType.FullName}' is not supported.");
    }

    public void Save<T>(string key, T value)
    {
        if (value is null)
        {
            _store.Values.Remove(key);
            return;
        }

        object boxedValue = value;
        Type targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (targetType.IsEnum)
        {
            _store.Values[key] = boxedValue.ToString();
            return;
        }

        if (boxedValue is string or bool or int or long or double or uint or ulong)
        {
            _store.Values[key] = boxedValue;
            return;
        }

        if (boxedValue is float floatValue)
        {
            _store.Values[key] = (double)floatValue;
            return;
        }

        throw new NotSupportedException($"Setting type '{targetType.FullName}' is not supported.");
    }
}
