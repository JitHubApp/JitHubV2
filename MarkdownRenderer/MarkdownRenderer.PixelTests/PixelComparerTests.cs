using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace MarkdownRenderer.PixelTests;

public sealed class PixelComparerTests
{
    [Fact]
    public void BrowserProcessJob_DisposeTerminatesLauncherAndDescendant()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mdr-job-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string childPidPath = Path.Combine(root, "child.pid");
        string scriptPath = Path.Combine(root, "spawn-child.ps1");
        string escapedChildPidPath = childPidPath.Replace("'", "''", StringComparison.Ordinal);
        File.WriteAllText(
            scriptPath,
            "$child = Start-Process -FilePath \"$env:SystemRoot\\System32\\ping.exe\" " +
            "-ArgumentList @(\"127.0.0.1\", \"-n\", \"30\") -PassThru -WindowStyle Hidden\n" +
            $"[System.IO.File]::WriteAllText('{escapedChildPidPath}', $child.Id.ToString())\n" +
            "$child.WaitForExit()\n");
        string powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell",
            "7",
            "pwsh.exe");
        if (!File.Exists(powerShell))
        {
            powerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = powerShell,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        BrowserProcessJob job = BrowserProcessJob.StartSuspended(startInfo, out Process process);
        Process? child = null;
        try
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (!File.Exists(childPidPath) && timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(10);
            }

            Assert.True(File.Exists(childPidPath), "The launcher did not publish its child PID.");
            int childPid = int.Parse(File.ReadAllText(childPidPath), CultureInfo.InvariantCulture);
            child = Process.GetProcessById(childPid);
            Assert.False(child.HasExited);
            Assert.False(process.HasExited);

            job.Dispose();
            Assert.True(process.WaitForExit(5_000));
            Assert.True(child.WaitForExit(5_000));
        }
        finally
        {
            job.Dispose();
            child?.Dispose();
            process.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CropToRequiredBounds_RejectsUndersizedCapture()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            PixelComparer.CropToRequiredBounds(new byte[4 * 2 * 2], 2, 2, 3, 2));

        Assert.Contains("does not cover", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CropToRequiredBounds_CropsOversizedCaptureToRequestedArea()
    {
        byte[] source = Enumerable.Range(0, 4 * 3 * 3)
            .Select(static value => (byte)value)
            .ToArray();

        byte[] cropped = PixelComparer.CropToRequiredBounds(source, 3, 3, 2, 2);

        Assert.Equal(4 * 2 * 2, cropped.Length);
        Assert.Equal(source.AsSpan(0, 8).ToArray(), cropped.AsSpan(0, 8).ToArray());
        Assert.Equal(source.AsSpan(12, 8).ToArray(), cropped.AsSpan(8, 8).ToArray());
    }

    [Fact]
    public void Compare_IgnoresUndefinedColorUnderFullyTransparentPixels()
    {
        PixelComparer.DiffReport report = PixelComparer.Compare(
            [255, 0, 0, 0],
            [0, 255, 255, 0],
            1,
            1,
            channelTolerance: 0);

        Assert.Equal(0, report.MaxChannelDelta);
        Assert.Equal(0, report.MeanChannelDelta);
        Assert.Equal(0, report.DifferingPixelFraction);
    }

    [Fact]
    public void Compare_WeightsVisibleColorByAlpha()
    {
        PixelComparer.DiffReport report = PixelComparer.Compare(
            [255, 0, 0, 128],
            [0, 0, 0, 128],
            1,
            1,
            channelTolerance: 0);

        Assert.Equal(128, report.MaxChannelDelta);
        Assert.Equal(32, report.MeanChannelDelta);
        Assert.Equal(1, report.DifferingPixelFraction);
    }

    [Fact]
    public void SaveRgbaAsPng_SupportsDeepArtifactPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mdr-pixel-path-{Guid.NewGuid():N}");
        string directory = root;
        while (directory.Length < 280)
        {
            directory = Path.Combine(directory, "nested-artifact-segment");
        }

        string path = Path.Combine(directory, "pixel.png");
        try
        {
            PixelComparer.SaveRgbaAsPng([255, 0, 0, 255], 1, 1, path);

            Assert.True(File.Exists(path));
            var (rgba, width, height) = PixelComparer.LoadPngAsRgba(path);
            Assert.Equal(1, width);
            Assert.Equal(1, height);
            Assert.Equal([255, 0, 0, 255], rgba);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
