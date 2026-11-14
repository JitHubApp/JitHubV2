using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Pixel.Tests.TestSupport;

/// <summary>
/// Wraps a headless Chromium-family browser (Edge or Chrome) as the
/// industry-standard SVG ground-truth renderer. The test process launches
/// the browser with <c>--headless --screenshot</c> against a tiny HTML
/// shim that hosts the SVG at exact pixel dimensions, captures the PNG,
/// and crops it to the requested rectangle.
///
/// If no Chromium binary is found, <see cref="TryFindBrowser"/> returns
/// null and the dependent xUnit theories self-skip via <c>Skip.IfNot</c>.
/// </summary>
public static class HeadlessBrowserRasterizer
{
    // Edge reserves roughly 102 logical pixels for browser-frame metrics when
    // a private profile is supplied, even in headless mode. The screenshot is
    // cropped to the requested SVG bounds by the caller, so a conservative
    // vertical allowance preserves the full content viewport without changing
    // the comparison dimensions.
    private const int BrowserFrameHeightAllowancePx = 128;

    private static readonly string[] Candidates =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    };

    /// <summary>Returns the path to a Chromium binary, or null if none found.</summary>
    public static string? TryFindBrowser()
    {
        foreach (var p in Candidates)
            if (File.Exists(p)) return p;
        return null;
    }

    // Keep browser launches serialized because Chromium still creates helper
    // processes for a headless screenshot. Every launch also receives its own
    // temporary profile below, so Edge cannot forward the command to the user's
    // running browser or contend for the default profile lock.
    private static readonly object BrowserLock = new();

    /// <summary>
    /// Renders <paramref name="svgBytes"/> with a Chromium headless browser
    /// at <paramref name="widthPx"/> × <paramref name="heightPx"/> pixels and
    /// returns the path to the captured PNG. Caller is responsible for
    /// deleting the returned file when finished.
    /// </summary>
    /// <param name="extraArgs">Browser CLI extras (e.g. <c>--force-color-profile=srgb</c>).</param>
    /// <returns>Path to the PNG file, or null if the browser invocation failed.</returns>
    public static string? Rasterize(byte[] svgBytes, int widthPx, int heightPx, string[]? extraArgs = null)
    {
        var browser = TryFindBrowser();
        if (browser is null) return null;

        var dir = Path.Combine(Path.GetTempPath(), "mdr-pixel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string svgPath = Path.Combine(dir, "fixture.svg");
        string pngPath = Path.Combine(dir, "out.png");
        string profilePath = Path.Combine(dir, "browser-profile");

        // Chromium will render the SVG directly when given a file:// URL to
        // a .svg file — no HTML wrapper, no <img>, no data URI. This avoids
        // both (a) data-URI security restrictions that strip url(#...)
        // references from <defs> and (b) HTML5 parsing differences that
        // miscompute the SVG's intrinsic size. The browser sizes the SVG to
        // the window using its native width/height attributes, so we set
        // --window-size to the requested pixel box.
        File.WriteAllBytes(svgPath, svgBytes);

        var psi = new ProcessStartInfo
        {
            FileName = browser,
            Arguments = BuildArguments(svgPath, pngPath, profilePath, widthPx, heightPx, extraArgs),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = dir,
        };

        Process? proc = null;
        BrowserProcessJob? processJob = null;
        bool success = false;
        lock (BrowserLock)
        try
        {
            processJob = BrowserProcessJob.StartSuspended(psi, out proc);
            if (!WaitForStableScreenshot(pngPath)) return null;
            success = true;
            return pngPath;
        }
        finally
        {
            // Closing the job kills the browser launcher and every descendant,
            // including Edge's detached helper process.
            try { processJob?.Dispose(); } catch { }
            if (proc is not null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                try { proc.Dispose(); } catch { }
            }
            if (!success)
            {
                // Best-effort cleanup of the temp profile + shim files when
                // we didn't produce a PNG (caller handles dir on success).
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    private static string BuildArguments(
        string svgPath,
        string pngPath,
        string profilePath,
        int widthPx,
        int heightPx,
        string[]? extraArgs)
    {
        var args = new System.Collections.Generic.List<string>
        {
            "--headless=new",
            // PR fidelity uses Edge's deterministic WARP path. `--disable-gpu`
            // is not equivalent: it selects a different software raster path
            // with measurably different edge coverage.
            "--use-angle=d3d11-warp",
            "--use-gl=angle",
            "--no-sandbox",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-search-engine-choice-screen",
            "--disable-background-networking",
            "--disable-component-update",
            "--disable-extensions",
            "--disable-sync",
            "--metrics-recording-only",
            "--disable-features=msEdgeFirstRunExperience,EdgeFirstRunExperience",
            "--hide-scrollbars",
            "--default-background-color=00000000",
            "--force-color-profile=srgb",
            "--virtual-time-budget=5000",
            $"--user-data-dir=\"{profilePath}\"",
            $"--screenshot=\"{pngPath}\"",
            $"--window-size={widthPx},{heightPx + BrowserFrameHeightAllowancePx}",
            "--force-device-scale-factor=1",
        };
        if (extraArgs is not null) args.AddRange(extraArgs);
        args.Add("\"file:///" + svgPath.Replace('\\', '/') + "\"");
        return string.Join(' ', args);
    }

    // Edge/Chrome stub launchers commonly exit immediately while the
    // real browser process continues to work in the background, so
    // WaitForExit on the launcher PID is unreliable. Wait for the PNG
    // file to materialize on disk instead, with a hard wall-clock cap.
    // Wait for the PNG to materialize *and* stabilize. Chromium writes
    // the screenshot file in stages — the first few bytes can appear
    // before the SVG's <defs>/url(#...) references resolve, so reading
    // too early yields a near-blank capture (radial gradients, filters,
    // patterns all hit this). We poll until the file size is the same
    // for several consecutive reads, which means the writer is done.
    private static bool WaitForStableScreenshot(string pngPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        long lastSize = -1;
        int stableCount = 0;
        const int requiredStable = 5; // ~500ms of no change
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pngPath))
            {
                long sz = new FileInfo(pngPath).Length;
                if (sz > 0 && sz == lastSize) { stableCount++; if (stableCount >= requiredStable) break; }
                else { stableCount = 0; lastSize = sz; }
            }
            System.Threading.Thread.Sleep(100);
        }
        if (!File.Exists(pngPath) || new FileInfo(pngPath).Length == 0) return false;
        // Extra grace so any final flush completes.
        System.Threading.Thread.Sleep(250);
        return true;
    }
}
