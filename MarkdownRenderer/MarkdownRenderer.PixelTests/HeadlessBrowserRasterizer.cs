using System;
using System.Diagnostics;
using System.IO;

namespace MarkdownRenderer.PixelTests;

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

    // Chromium's --screenshot path produces incorrect output when --user-data-dir
    // points to a freshly-created profile (first-run UI consumes pixels in the
    // captured PNG even in headless=new mode). The robust workaround is to omit
    // --user-data-dir entirely, but multiple concurrent instances then share the
    // default profile and corrupt each other's output. Serializing browser calls
    // through this lock gives us correct screenshots without that race.
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

        // Chromium will render the SVG directly when given a file:// URL to
        // a .svg file — no HTML wrapper, no <img>, no data URI. This avoids
        // both (a) data-URI security restrictions that strip url(#...)
        // references from <defs> and (b) HTML5 parsing differences that
        // miscompute the SVG's intrinsic size. The browser sizes the SVG to
        // the window using its native width/height attributes, so we set
        // --window-size to the requested pixel box.
        File.WriteAllBytes(svgPath, svgBytes);

        var args = new System.Collections.Generic.List<string>
        {
            "--headless=new",
            "--disable-gpu",
            "--no-sandbox",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-search-engine-choice-screen",
            "--hide-scrollbars",
            "--default-background-color=00000000",
            "--virtual-time-budget=5000",
            $"--screenshot=\"{pngPath}\"",
            $"--window-size={widthPx},{heightPx}",
            "--force-device-scale-factor=1",
        };
        if (extraArgs is not null) args.AddRange(extraArgs);
        args.Add("\"file:///" + svgPath.Replace('\\', '/') + "\"");

        var psi = new ProcessStartInfo
        {
            FileName = browser,
            Arguments = string.Join(' ', args),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = dir,
        };

        Process? proc = null;
        bool success = false;
        lock (BrowserLock)
        try
        {
            proc = Process.Start(psi);
            if (proc is null) return null;
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
            if (!File.Exists(pngPath) || new FileInfo(pngPath).Length == 0) return null;
            // Extra grace so any final flush completes.
            System.Threading.Thread.Sleep(250);
            success = true;
            return pngPath;
        }
        finally
        {
            // Always tear down the launcher; on timeout the entire process
            // tree (real browser child) needs killing or it leaks.
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

}
