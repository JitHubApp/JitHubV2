using System.Globalization;
using System.Text;
using System.Xml;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgAnimationInitialValueTests
{
    [Fact]
    public void SpanIterationPreservesExistingFirstFrameTokenSemantics()
    {
        string[] valuesCases = ["a", "a;b", "a;b;c", "a;", ";a", ";", " a ; b ; c "];
        string[] timesCases = ["", " ", "0", "0;0", "0;0;1", "0;1;0", "1;0", "-1;0", "bad;0", "0;", ";0", "NaN;0", "0;0;0;0"];
        foreach (string values in valuesCases)
        {
            foreach (string times in timesCases)
            {
                XmlElement animation = CreateAnimation(values, times);

                string? actual = SvgStaticSnapshot.GetInitialValue(animation, CancellationToken.None);

                Assert.Equal(LegacyInitialValue(values, times), actual);
            }
        }
    }

    [Theory]
    [InlineData("animate", "", "0.5", "0.9", "0.5")]
    [InlineData("animate", " ", "", "0.9", null)]
    [InlineData("set", "", "", "0.9", "0.9")]
    [InlineData("set", " ", "0.5", "0.9", "0.5")]
    public void MissingValuesPreservesFromAndSetFallback(string element, string values, string from, string to, string? expected)
    {
        XmlElement animation = CreateAnimation(values, "", element);
        animation.SetAttribute("from", from);
        animation.SetAttribute("to", to);

        Assert.Equal(expected, SvgStaticSnapshot.GetInitialValue(animation, CancellationToken.None));
    }

    [Fact]
    public void ManyImmediateValuesAllocateOnlyTheSelectedToken()
    {
        string prefix = string.Concat(Enumerable.Repeat("0;", 131_072));
        XmlElement animation = CreateAnimation(prefix + "0.25", prefix + "0");
        Assert.Equal("0.25", SvgStaticSnapshot.GetInitialValue(animation, CancellationToken.None));

        long before = GC.GetAllocatedBytesForCurrentThread();
        string? result = SvgStaticSnapshot.GetInitialValue(animation, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("0.25", result);
        Assert.True(allocated < 8192, $"Initial-value selection allocated {allocated} bytes.");
    }

    [Fact]
    public void UnusedValuesDoNotBecomeAnArrayOfEmptyTokens()
    {
        XmlElement animation = CreateAnimation("0.25;" + new string(';', 1024 * 1024), "");
        Assert.Equal("0.25", SvgStaticSnapshot.GetInitialValue(animation, CancellationToken.None));

        long before = GC.GetAllocatedBytesForCurrentThread();
        string? result = SvgStaticSnapshot.GetInitialValue(animation, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("0.25", result);
        Assert.True(allocated < 8192, $"Unused-value selection allocated {allocated} bytes.");
    }

    [Fact]
    public void CancellationIsObservedBeforeScanningValues()
    {
        XmlElement animation = CreateAnimation("0;1", "0;0");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SvgStaticSnapshot.GetInitialValue(animation, cancellation.Token));
    }

    [Fact]
    public void FullPreparationKeepsLastImmediateValueAndRemovesAnimation()
    {
        byte[] source = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'>" +
            "<rect opacity='1'><animate attributeName='opacity' values='0.1;0.5;0.9' keyTimes='0;0;1'/></rect></svg>");

        string prepared = Encoding.UTF8.GetString(SvgStaticSnapshot.Create(source, CancellationToken.None));

        Assert.Contains("opacity=\"0.5\"", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("<animate", prepared, StringComparison.OrdinalIgnoreCase);
    }

    private static XmlElement CreateAnimation(string values, string times, string element = "animate")
    {
        var document = new XmlDocument();
        XmlElement animation = document.CreateElement(element, "http://www.w3.org/2000/svg");
        animation.SetAttribute("values", values);
        animation.SetAttribute("keyTimes", times);
        return animation;
    }

    // Small differential inputs preserve the pre-existing static-image contract,
    // including empty tokens and immediate/invalid key-time sequences.
    private static string LegacyInitialValue(string values, string keyTimes)
    {
        string[] candidates = values.Split(';');
        int selected = 0;
        if (!string.IsNullOrWhiteSpace(keyTimes))
        {
            string[] times = keyTimes.Split(';');
            for (int index = 0; index < Math.Min(times.Length, candidates.Length); index++)
            {
                if (!double.TryParse(times[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double time) || time > 0)
                    break;
                selected = index;
            }
        }
        return candidates[selected];
    }
}
