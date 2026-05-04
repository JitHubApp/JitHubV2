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
        try
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
        catch (Exception ex) when (IsRecoverableSettingReadException(ex))
        {
            return default!;
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

        if (TryConvertStoredValue(value, out T typedValue))
        {
            return typedValue;
        }

        return default!;
    }

    private static bool TryConvertStoredValue<T>(object value, out T typedValue)
    {
        typedValue = default!;
        if (value is T directValue)
        {
            typedValue = directValue;
            return true;
        }

        Type targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (value is string stringValue)
        {
            if (targetType == typeof(string))
            {
                typedValue = (T)(object)stringValue;
                return true;
            }

            if (targetType.IsEnum)
            {
                if (Enum.TryParse(targetType, stringValue, true, out object? enumValue))
                {
                    typedValue = (T)enumValue;
                    return true;
                }

                return false;
            }

            return TryParseScalar(stringValue, out typedValue);
        }

        if (targetType.IsEnum)
        {
            try
            {
                typedValue = (T)Enum.ToObject(targetType, value);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (value is IConvertible)
        {
            try
            {
                typedValue = (T)Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                return false;
            }
        }

        return false;
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
            try
            {
                string key = Encoding.UTF8.GetString(Convert.FromBase64String(encodedKey));
                string value = Encoding.UTF8.GetString(Convert.FromBase64String(encodedValue));
                _fileValues[key] = value;
            }
            catch (FormatException)
            {
                // Ignore corrupted legacy fallback settings instead of failing app startup.
            }
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
        return TryParseScalar(serializedValue, out T parsedValue)
            ? parsedValue
            : default!;
    }

    private static bool TryParseScalar<T>(string serializedValue, out T parsedValue)
    {
        Type targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        parsedValue = default!;

        object? result = targetType switch
        {
            _ when targetType == typeof(string) => serializedValue,
            _ when targetType == typeof(bool) && bool.TryParse(serializedValue, out bool boolValue) => boolValue,
            _ when targetType == typeof(int) && int.TryParse(serializedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue) => intValue,
            _ when targetType == typeof(long) && long.TryParse(serializedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue) => longValue,
            _ when targetType == typeof(double) && double.TryParse(serializedValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double doubleValue) => doubleValue,
            _ when targetType == typeof(float) && float.TryParse(serializedValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float floatValue) => floatValue,
            _ when targetType == typeof(uint) && uint.TryParse(serializedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uintValue) => uintValue,
            _ when targetType == typeof(ulong) && ulong.TryParse(serializedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong ulongValue) => ulongValue,
            _ when targetType.IsEnum && Enum.TryParse(targetType, serializedValue, true, out object? enumValue) => enumValue,
            _ => null
        };

        if (result is null)
        {
            return false;
        }

        parsedValue = (T)result;
        return true;
    }

    private static bool IsRecoverableSettingReadException(Exception ex)
    {
        return ex is FormatException
            or InvalidCastException
            or OverflowException
            or ArgumentException
            or NotSupportedException;
    }
}
