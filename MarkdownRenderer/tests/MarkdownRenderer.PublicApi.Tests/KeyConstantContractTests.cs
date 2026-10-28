using System.Reflection;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.PublicApi.Tests;

/// <summary>
/// Hosts look up resources and localized strings by these public keys, so
/// no two keys may share a value. PublicAPI.*.txt pins each value; this
/// checks that the values stay distinct.
/// </summary>
public sealed class KeyConstantContractTests
{
    [Theory]
    [InlineData(typeof(MarkdownResourceKeys))]
    [InlineData(typeof(MarkdownStringKeys))]
    public void KeyConstants_AreNonEmptyAndUnique(Type keys)
    {
        string[] values = keys
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(values);
        Assert.All(values, static value => Assert.False(string.IsNullOrWhiteSpace(value)));
        string[] duplicates = values
            .GroupBy(static value => value, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        Assert.True(duplicates.Length == 0, $"{keys.Name} reuses: {string.Join(", ", duplicates)}");
    }
}
