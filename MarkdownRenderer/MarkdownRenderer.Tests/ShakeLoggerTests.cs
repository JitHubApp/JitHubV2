using MarkdownRenderer.Diagnostics;
using Xunit;

namespace MarkdownRenderer.Tests;

public class ShakeLoggerTests
{
    [Fact]
    public void DisabledLoggerDoesNotAdvanceFrameCounter()
    {
        bool wasEnabled = ShakeLogger.Enabled;
        try
        {
            ShakeLogger.Enabled = false;
            long before = ShakeLogger.CurrentFrame;

            long frame = ShakeLogger.NextFrame();

            Assert.Equal(0, frame);
            Assert.Equal(before, ShakeLogger.CurrentFrame);
        }
        finally
        {
            ShakeLogger.Enabled = wasEnabled;
        }
    }

    [Fact]
    public void LoggerCanBeEnabledFromDiagnosticsEnvironmentVariable()
    {
        bool wasEnabled = ShakeLogger.Enabled;
        string? oldDiagnostics = Environment.GetEnvironmentVariable(ShakeLogger.DiagnosticsEnvironmentVariable);
        string? oldShakeLog = Environment.GetEnvironmentVariable(ShakeLogger.ShakeLogEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ShakeLogger.DiagnosticsEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable(ShakeLogger.ShakeLogEnvironmentVariable, null);

            Assert.True(ShakeLogger.ConfigureFromEnvironment());
            Assert.True(ShakeLogger.IsEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ShakeLogger.DiagnosticsEnvironmentVariable, oldDiagnostics);
            Environment.SetEnvironmentVariable(ShakeLogger.ShakeLogEnvironmentVariable, oldShakeLog);
            ShakeLogger.Enabled = wasEnabled;
        }
    }
}
