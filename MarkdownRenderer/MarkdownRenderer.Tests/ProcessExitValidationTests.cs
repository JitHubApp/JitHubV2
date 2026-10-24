using MarkdownRenderer.Sample.Automation;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class ProcessExitValidationTests
{
    [Fact]
    public void CleanExitRequiresBothExitCodeAndActualTeardown()
    {
        Assert.Null(ProcessExitValidation.GetFailure(true, 0, true));
        Assert.NotNull(ProcessExitValidation.GetFailure(true, 0, false));
        Assert.NotNull(ProcessExitValidation.GetFailure(true, null, true));
        Assert.NotNull(ProcessExitValidation.GetFailure(false, null, false));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(unchecked((int)0xC0000409))]
    [InlineData(unchecked((int)0xC0000005))]
    public void NativeCrashesCannotPassEvenWithADisposalSignal(int exitCode)
        => Assert.NotNull(ProcessExitValidation.GetFailure(true, exitCode, true));
}
