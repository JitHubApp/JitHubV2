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

        // Write the SVG to a temp HTML shim — base64-embed so we don't
        // need a second file and there are no path-escaping concerns.
        // The body has no padding/margin and the SVG is set to the exact
        // pixel size; the screenshot then crops to those exact bounds.
        string b64 = Convert.ToBase64String(svgBytes);
        string html =
            "<!doctype html><html><head><meta charset=\"utf-8\"><style>" +
            "html,body{margin:0;padding:0;background:transparent;}" +
            $"img{{display:block;width:{widthPx}px;height:{heightPx}px;}}" +
            "</style></head><body>" +
            $"<img src=\"data:image/svg+xml;base64,{b64}\"/>" +
            "</body></html>";

        var dir = Path.Combine(Path.GetTempPath(), "mdr-pixel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string htmlPath = Path.Combine(dir, "shim.html");
        string pngPath = Path.Combine(dir, "out.png");
        File.WriteAllText(htmlPath, html);

        var args = new System.Collections.Generic.List<string>
        {
            "--headless=new",
            "--disable-gpu",
            "--no-sandbox",
            "--hide-scrollbars",
            "--default-background-color=00000000",
            // Singleton-process lock can race with the user's own browser
            // session; isolate to a per-invocation profile.
            $"--user-data-dir=\"{Path.Combine(dir, "udd")}\"",
            // Without a virtual-time budget, headless Chromium tears down
            // before sub-resources (the data-URI <img>) are decoded, and
            // no PNG ever lands on disk.
            "--virtual-time-budget=2000",
            $"--screenshot=\"{pngPath}\"",
            $"--window-size={widthPx},{heightPx}",
            "--force-device-scale-factor=1",
        };
        if (extraArgs is not null) args.AddRange(extraArgs);
        args.Add("\"file:///" + htmlPath.Replace('\\', '/') + "\"");

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

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        // Edge/Chrome stub launchers commonly exit immediately while the
        // real browser process continues to work in the background, so
        // WaitForExit on the launcher PID is unreliable. Wait for the PNG
        // file to materialize on disk instead, with a hard wall-clock cap.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pngPath) && new FileInfo(pngPath).Length > 0) break;
            System.Threading.Thread.Sleep(100);
        }
        if (!File.Exists(pngPath)) return null;
        // Tiny grace period for the browser to finish flushing the PNG.
        System.Threading.Thread.Sleep(150);
        return pngPath;
    }
}
