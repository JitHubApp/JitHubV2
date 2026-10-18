using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Windows.Storage;

namespace JitHub.Services;

public class SettingService : ISettingService
{
    private readonly ApplicationDataContainer? _store;
    private readonly string? _settingsFilePath;
    private readonly object _fileGate = new();
    private Dictionary<string, string?>? _fileValues;

    public SettingService()
    {
        try
        {
            _store = ApplicationData.Current.LocalSettings;
        }
        catch
        {
            string settingsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JitHub");
            Directory.CreateDirectory(settingsDirectory);
            _settingsFilePath = Path.Combine(settingsDirectory, "settings.json");
        }
    }

    public T Get<T>(string key)
    {
        if (_store is not null)
        {
            return GetFromApplicationData<T>(key);
        }

        lock (_fileGate)
        {
            EnsureFileValuesLoaded();
            if (_fileValues is null || !_fileValues.TryGetValue(key, out string? serializedValue) || serializedValue is null)
            {
                return default!;
            }

            return ParseScalar<T>(serializedValue);
        }
    }

    public void Save<T>(string key, T value)
    {
        if (_store is not null)
        {
            SaveToApplicationData(key, value);
            return;
        }

        lock (_fileGate)
        {
            EnsureFileValuesLoaded();
            if (_fileValues is null)
            {
                _fileValues = new Dictionary<string, string?>(StringComparer.Ordinal);
            }

            if (value is null)
            {
                _fileValues.Remove(key);
            }
            else
            {
                _fileValues[key] = SerializeScalar(value, typeof(T));
            }

            PersistFileValues();
        }
    }

    private T GetFromApplicationData<T>(string key)
    {
        if (_store is null || !_store.Values.TryGetValue(key, out object? value) || value is null)
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

    private void SaveToApplicationData<T>(string key, T value)
    {
        if (_store is null)
        {
            return;
        }

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

    private void EnsureFileValuesLoaded()
    {
        if (_fileValues is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settingsFilePath) || !File.Exists(_settingsFilePath))
        {
            _fileValues = new Dictionary<string, string?>(StringComparer.Ordinal);
            return;
        }

        _fileValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(_settingsFilePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int separatorIndex = line.IndexOf('|');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string encodedKey = line[..separatorIndex];
            string encodedValue = line[(separatorIndex + 1)..];
            string key = Encoding.UTF8.GetString(Convert.FromBase64String(encodedKey));
            string value = Encoding.UTF8.GetString(Convert.FromBase64String(encodedValue));
            _fileValues[key] = value;
        }
    }

    private void PersistFileValues()
    {
        if (string.IsNullOrWhiteSpace(_settingsFilePath))
        {
            return;
        }

        IEnumerable<string> lines = (_fileValues ?? new Dictionary<string, string?>())
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
                $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key))}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value ?? string.Empty))}");
        File.WriteAllLines(_settingsFilePath, lines);
    }

    private static string SerializeScalar<T>(T value, Type declaredType)
    {
        object boxedValue = value!;
        Type targetType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        if (targetType.IsEnum)
        {
            return boxedValue.ToString() ?? string.Empty;
        }

        return boxedValue switch
        {
            string stringValue => stringValue,
            bool boolValue => boolValue ? "true" : "false",
            float floatValue => floatValue.ToString(CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString(CultureInfo.InvariantCulture),
            _ when boxedValue is IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => throw new NotSupportedException($"Setting type '{targetType.FullName}' is not supported.")
        };
    }

    private static T ParseScalar<T>(string serializedValue)
    {
        Type targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        object result = targetType switch
        {
            _ when targetType == typeof(string) => serializedValue,
            _ when targetType == typeof(bool) => bool.Parse(serializedValue),
            _ when targetType == typeof(int) => int.Parse(serializedValue, CultureInfo.InvariantCulture),
            _ when targetType == typeof(long) => long.Parse(serializedValue, CultureInfo.InvariantCulture),
            _ when targetType == typeof(double) => double.Parse(serializedValue, CultureInfo.InvariantCulture),
            _ when targetType == typeof(float) => float.Parse(serializedValue, CultureInfo.InvariantCulture),
            _ when targetType == typeof(uint) => uint.Parse(serializedValue, CultureInfo.InvariantCulture),
            _ when targetType == typeof(ulong) => ulong.Parse(serializedValue, CultureInfo.InvariantCulture),
            _ when targetType.IsEnum => Enum.Parse(targetType, serializedValue),
            _ => throw new NotSupportedException($"Setting type '{targetType.FullName}' is not supported.")
        };

        return (T)result;
    }
}
