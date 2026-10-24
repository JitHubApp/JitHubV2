using System.Globalization;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownLocalizationSnapshotTests
{
    [Fact]
    public void CommittedCultureAndProviderRemainOneAtomicEffectivePair()
    {
        var oldProvider = new TestStringProvider();
        var newProvider = new TestStringProvider();
        MarkdownLocalizationSnapshot committed = MarkdownLocalizationSnapshot.Capture(
            "fr-FR",
            null,
            oldProvider,
            CultureInfo.GetCultureInfo("en-US"));
        MarkdownLocalizationSnapshot requested = MarkdownLocalizationSnapshot.Capture(
            "tr-TR",
            null,
            newProvider,
            CultureInfo.GetCultureInfo("en-US"));

        MarkdownLocalizationSnapshot? whileRebuilding =
            MarkdownLocalizationSnapshot.SelectCommitted(true, committed);
        MarkdownLocalizationSnapshot? beforeFirstCommit =
            MarkdownLocalizationSnapshot.SelectCommitted(false, committed);

        Assert.NotNull(whileRebuilding);
        Assert.Equal("fr-FR", whileRebuilding.Culture.Name);
        Assert.Same(oldProvider, whileRebuilding.StringProvider);
        Assert.Null(beforeFirstCommit);
        Assert.Equal("tr-TR", requested.Culture.Name);
        Assert.Same(newProvider, requested.StringProvider);
    }

    [Fact]
    public void InvalidRequestedLanguageFallsBackWithoutSplittingProviderFromCulture()
    {
        var provider = new TestStringProvider();
        MarkdownLocalizationSnapshot snapshot = MarkdownLocalizationSnapshot.Capture(
            "\0",
            "de-DE",
            provider,
            CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("en-US", snapshot.Culture.Name);
        Assert.Same(provider, snapshot.StringProvider);
    }

    private sealed class TestStringProvider : IMarkdownStringProvider
    {
        public string? GetString(string key, CultureInfo culture) => null;
    }
}
