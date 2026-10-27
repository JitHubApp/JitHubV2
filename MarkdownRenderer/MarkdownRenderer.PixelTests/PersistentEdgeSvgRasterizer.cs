using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MarkdownRenderer.PixelTests;

/// <summary>
/// Reuses one isolated headless Edge process for the SVG pixel corpus. Each
/// render still navigates to the exact fixture bytes on disk and captures an
/// unmodified CSS viewport at the requested device scale factor.
/// </summary>
public sealed class PersistentEdgeSvgRasterizer : Xunit.IAsyncLifetime
{
    private const int MaximumCssDimension = 16_384;
    private const int MaximumPhysicalDimension = 32_768;
    private const long MaximumPhysicalPixels = 64L * 1024 * 1024;
    private const int MaximumSvgBytes = 8 * 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _browserPath;
    private BrowserSession? _session;
    private bool _disposed;
    internal string? BackendEvidenceJson => _session?.BackendEvidenceJson;

    public PersistentEdgeSvgRasterizer()
    {
        // Finding the executable is deliberately the only constructor work.
        // Some test environments have no browser, and discovery must not
        // create a process/profile as an incidental fixture-construction side effect.
        _browserPath = HeadlessBrowserRasterizer.TryFindBrowser();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    internal static int CaptureCssExtent(int cssExtent, double scale) =>
        checked((int)Math.Ceiling(Math.Ceiling(cssExtent * scale) / scale));

    internal static byte[] ExtractRequiredCapture(byte[] png, int cssWidth, int cssHeight, double scale)
    {
        int requiredWidth = checked((int)Math.Ceiling(cssWidth * scale));
        int requiredHeight = checked((int)Math.Ceiling(cssHeight * scale));
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length >= 24 &&
            png.AsSpan(0, 8).SequenceEqual(signature) &&
            png.AsSpan(12, 4).SequenceEqual("IHDR"u8) &&
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) == requiredWidth &&
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) == requiredHeight)
        {
            // Most corpus cases need no crop. Do not decode/copy/re-encode an
            // entire raster just to return the already exact screenshot.
            return png;
        }
        var (pixels, width, height) = PixelComparer.LoadPngAsRgba(png);
        byte[] required = PixelComparer.CropToRequiredBounds(
            pixels, width, height, requiredWidth, requiredHeight);
        return width == requiredWidth && height == requiredHeight
            ? png
            : PixelComparer.EncodeRgbaAsPng(required, requiredWidth, requiredHeight);
    }

    public async Task<byte[]> RasterizeAsync(byte[] source, int cssWidth, int cssHeight, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateRequest(source.Length, cssWidth, cssHeight, scale);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_browserPath is null)
            {
                throw new InvalidOperationException("No supported headless Edge/Chrome executable was found.");
            }

            _session ??= await BrowserSession.StartAsync(_browserPath).ConfigureAwait(false);
            try
            {
                return await _session.RasterizeAsync(source, cssWidth, cssHeight, scale).ConfigureAwait(false);
            }
            catch
            {
                // A navigation, CDP, or screenshot failure leaves uncertain
                // page state. Retire this browser before another fixture can
                // accidentally consume a stale response or viewport.
                BrowserSession failed = _session;
                _session = null;
                await failed.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            BrowserSession? session = _session;
            _session = null;
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // SemaphoreSlim has no native handle unless WaitHandle is
            // requested; retaining it also keeps repeated fixture disposal
            // safely idempotent.
            _gate.Release();
        }
    }

    private static void ValidateRequest(int sourceLength, int cssWidth, int cssHeight, double scale)
    {
        if (sourceLength is <= 0 or > MaximumSvgBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceLength), "SVG source must be between 1 byte and 8 MiB.");
        }

        if (cssWidth is <= 0 or > MaximumCssDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(cssWidth));
        }

        if (cssHeight is <= 0 or > MaximumCssDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(cssHeight));
        }

        if (!double.IsFinite(scale) || scale is <= 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be finite and in the interval (0, 4].");
        }

        double physicalWidth = Math.Ceiling(cssWidth * scale);
        double physicalHeight = Math.Ceiling(cssHeight * scale);
        if (physicalWidth > MaximumPhysicalDimension || physicalHeight > MaximumPhysicalDimension ||
            physicalWidth * physicalHeight > MaximumPhysicalPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Requested browser raster exceeds the test oracle's bounded output size.");
        }
    }

    private sealed class BrowserSession : IAsyncDisposable
    {
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
        private const int MaximumCdpMessageBytes = 96 * 1024 * 1024;

        private readonly string _directory;
        private readonly string _profileDirectory;
        private readonly string _fixtureDirectory;
        private readonly Process _process;
        private readonly BrowserProcessJob _processJob;
        private readonly HttpClient _httpClient;
        private readonly ClientWebSocket _socket;
        private readonly CancellationTokenSource _readerCancellation = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly ConcurrentQueue<string> _lifecycleEvents = new();
        private readonly Task _readerTask;
        private string? _pageSessionId;
        private long _fixtureId;
        private int _nextCommandId;
        private int _disposed;
        internal string? BackendEvidenceJson { get; private set; }

        private BrowserSession(
            string directory,
            Process process,
            BrowserProcessJob processJob,
            HttpClient httpClient,
            ClientWebSocket socket)
        {
            _directory = directory;
            _profileDirectory = Path.Combine(directory, "profile");
            _fixtureDirectory = Path.Combine(directory, "fixtures");
            _process = process;
            _processJob = processJob;
            _httpClient = httpClient;
            _socket = socket;
            _readerTask = Task.Run(ReadMessagesAsync);
        }

        public static async Task<BrowserSession> StartAsync(string browserPath)
        {
            string directory = Path.Combine(Path.GetTempPath(), "mdr-edge-cdp-" + Guid.NewGuid().ToString("N"));
            string profileDirectory = Path.Combine(directory, "profile");
            string fixtureDirectory = Path.Combine(directory, "fixtures");
            Directory.CreateDirectory(profileDirectory);
            Directory.CreateDirectory(fixtureDirectory);

            Process? process = null;
            BrowserProcessJob? processJob = null;
            HttpClient? httpClient = null;
            ClientWebSocket? socket = null;
            BrowserSession? session = null;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = BuildBrowserArguments(profileDirectory),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = directory,
                };

                processJob = BrowserProcessJob.StartSuspended(startInfo, out process);
                httpClient = new HttpClient(new SocketsHttpHandler
                {
                    UseProxy = false,
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                })
                {
                    Timeout = TimeSpan.FromSeconds(3),
                };
                socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

                string versionUrl = await WaitForDevToolsEndpointAsync(profileDirectory, httpClient).ConfigureAwait(false);
                using JsonDocument version = JsonDocument.Parse(
                    await httpClient.GetStringAsync(versionUrl).ConfigureAwait(false));
                string webSocketUrl = version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
                    ?? throw new InvalidDataException("The browser did not publish a DevTools websocket URL.");

                using var connectTimeout = new CancellationTokenSource(StartupTimeout);
                await socket.ConnectAsync(new Uri(webSocketUrl), connectTimeout.Token).ConfigureAwait(false);
                session = new BrowserSession(
                    directory,
                    process ?? throw new InvalidOperationException("Browser process startup returned no process handle."),
                    processJob ?? throw new InvalidOperationException("Browser process startup returned no job handle."),
                    httpClient ?? throw new InvalidOperationException("Browser HTTP client setup failed."),
                    socket ?? throw new InvalidOperationException("Browser websocket setup failed."));
                process = null;
                processJob = null;
                httpClient = null;
                socket = null;

                // A dedicated context keeps fixture pages independent of the
                // browser profile's startup/default-tab lifecycle and state.
                JsonElement context = await session.SendCommandAsync(
                    "Target.createBrowserContext", new { disposeOnDetach = true }).ConfigureAwait(false);
                string browserContextId = context.GetProperty("browserContextId").GetString()
                    ?? throw new InvalidDataException("Edge did not create an isolated browser context.");
                JsonElement created = await session.SendCommandAsync("Target.createTarget", new
                {
                    url = "about:blank", browserContextId, background = false, newWindow = true,
                }).ConfigureAwait(false);
                string targetId = created.GetProperty("targetId").GetString()
                    ?? throw new InvalidDataException("The browser did not create a DevTools page target.");
                JsonElement attached = await session.SendCommandAsync(
                    "Target.attachToTarget",
                    new { targetId, flatten = true }).ConfigureAwait(false);
                session._pageSessionId = attached.GetProperty("sessionId").GetString()
                    ?? throw new InvalidDataException("The browser did not attach a DevTools page session.");
                await session.SendPageCommandAsync("Page.enable", new { }).ConfigureAwait(false);
                await session.SendPageCommandAsync("Runtime.enable", new { }).ConfigureAwait(false);
                await session.SendPageCommandAsync("Page.bringToFront", new { }).ConfigureAwait(false);
                await session.SendPageCommandAsync("Emulation.setDefaultBackgroundColorOverride", new
                {
                    color = new { r = 0, g = 0, b = 0, a = 0 },
                }).ConfigureAwait(false);
                return session;
            }
            catch
            {
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    try { socket?.Abort(); } catch { }
                    socket?.Dispose();
                    httpClient?.Dispose();
                    try { processJob?.Dispose(); } catch { }
                    if (process is not null)
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                        process.Dispose();
                    }

                    try { Directory.Delete(directory, recursive: true); } catch { }
                }

                throw;
            }
        }

        public async Task<byte[]> RasterizeAsync(byte[] source, int cssWidth, int cssHeight, double scale)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            string fixturePath = Path.Combine(_fixtureDirectory, $"fixture-{Interlocked.Increment(ref _fixtureId):D8}.svg");
            await File.WriteAllBytesAsync(fixturePath, source).ConfigureAwait(false);
            try
            {
                await SendPageCommandAsync("Emulation.setDeviceMetricsOverride", new
                {
                    width = cssWidth,
                    height = cssHeight,
                    deviceScaleFactor = scale,
                    mobile = false,
                    screenWidth = cssWidth,
                    screenHeight = cssHeight,
                }).ConfigureAwait(false);

                string fileUrl = new Uri(fixturePath).AbsoluteUri;
                JsonElement navigation = await SendPageCommandAsync("Page.navigate", new { url = fileUrl })
                    .ConfigureAwait(false);
                if (navigation.TryGetProperty("errorText", out JsonElement navigationError))
                {
                    throw new InvalidOperationException("Edge failed to navigate to the SVG fixture: " + navigationError.GetString());
                }

                await WaitForDocumentReadyAsync(fileUrl, cssWidth, cssHeight, scale).ConfigureAwait(false);
                JsonElement capture = await SendPageCommandAsync("Page.captureScreenshot", new
                {
                    format = "png",
                    fromSurface = true,
                    // Older Edge versions truncate fractional clip DIPs before
                    // scaling them. An integral CSS capture extent covers the
                    // complete allocation without changing SVG/layout/DPR.
                    // ExtractRequiredCapture retains every required physical
                    // pixel; it never shrinks comparison to overlapping ink.
                    captureBeyondViewport = true,
                    omitBackground = true,
                    clip = new
                    {
                        x = 0, y = 0,
                        width = CaptureCssExtent(cssWidth, scale),
                        height = CaptureCssExtent(cssHeight, scale),
                        scale = 1,
                    },
                }).ConfigureAwait(false);
                string encoded = capture.GetProperty("data").GetString()
                    ?? throw new InvalidDataException("Edge returned an empty screenshot payload.");
                byte[] png = ExtractRequiredCapture(Convert.FromBase64String(encoded), cssWidth, cssHeight, scale);
                ValidatePngDimensions(png, cssWidth, cssHeight, scale);
                await VerifyBackendAsync().ConfigureAwait(false);
                return png;
            }
            finally
            {
                try { File.Delete(fixturePath); } catch { }
            }
        }

        private async Task VerifyBackendAsync()
        {
            if (BackendEvidenceJson is not null) return;
            JsonElement systemInfo = await SendCommandAsync("SystemInfo.getInfo", new { }).ConfigureAwait(false);
            JsonElement version = await SendCommandAsync("Browser.getVersion", new { }).ConfigureAwait(false);
            string evidence = JsonSerializer.Serialize(new { browser = version, systemInfo });
            JsonElement gpu = systemInfo.GetProperty("gpu");
            if (!gpu.TryGetProperty("auxAttributes", out JsonElement attributes) ||
                !attributes.TryGetProperty("glImplementationParts", out JsonElement implementation) ||
                implementation.GetString()?.Contains("angle=d3d11-warp", StringComparison.Ordinal) != true)
            {
                throw new InvalidOperationException("Edge did not report the required D3D11 WARP backend: " + evidence);
            }
            BackendEvidenceJson = evidence;
        }

        private async Task WaitForDocumentReadyAsync(string expectedUrl, int width, int height, double scale)
        {
            string encodedUrl = JsonSerializer.Serialize(expectedUrl);
            string expression = $$"""
                (async () => {
                  const expected = {{encodedUrl}};
                  if (location.href !== expected) {
                    return { href: location.href, retry: true };
                  }
                  const deadline = performance.now() + 25000;
                  while (document.readyState !== 'complete') {
                    if (performance.now() >= deadline) throw new Error('Document readiness timed out.');
                    await new Promise(resolve => setTimeout(resolve, 20));
                  }
                  if (document.fonts && document.fonts.ready) await document.fonts.ready;
                  const imageWaits = Array.from(document.querySelectorAll('image')).map(element => {
                    const href = element.href && element.href.baseVal;
                    if (!href || href.startsWith('#')) return Promise.resolve();
                    const image = new Image();
                    image.src = href;
                    return image.decode();
                  });
                  await Promise.all(imageWaits);
                  await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                  return {
                    href: location.href,
                    readyState: document.readyState,
                    width: innerWidth,
                    height: innerHeight,
                    devicePixelRatio,
                    imageCount: imageWaits.length
                  };
                })()
                """;

            long deadline = Environment.TickCount64 + (long)StartupTimeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                TimeSpan remaining = TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64));
                JsonElement result;
                try
                {
                    result = await SendPageCommandAsync("Runtime.evaluate", new
                    {
                        expression,
                        awaitPromise = true,
                        returnByValue = true,
                        userGesture = false,
                    }, remaining < CommandTimeout ? remaining : CommandTimeout).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (IsTransientNavigationError(ex.Message))
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    continue;
                }

                if (result.TryGetProperty("exceptionDetails", out JsonElement exception))
                {
                    string detail = exception.TryGetProperty("text", out JsonElement text)
                        ? text.GetString() ?? "unknown JavaScript exception"
                        : exception.ToString();
                    if (IsTransientNavigationError(detail))
                    {
                        await Task.Delay(50).ConfigureAwait(false);
                        continue;
                    }

                    throw new InvalidOperationException("Edge fixture readiness failed: " + detail);
                }

                JsonElement value = result.GetProperty("result").GetProperty("value");
                string actualUrl = value.GetProperty("href").GetString() ?? string.Empty;
                if (value.TryGetProperty("retry", out JsonElement retry) && retry.GetBoolean())
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    continue;
                }

                int actualWidth = value.GetProperty("width").GetInt32();
                int actualHeight = value.GetProperty("height").GetInt32();
                double actualScale = value.GetProperty("devicePixelRatio").GetDouble();
                if (!string.Equals(actualUrl, expectedUrl, StringComparison.Ordinal) ||
                    actualWidth != width || actualHeight != height || Math.Abs(actualScale - scale) > 0.000001)
                {
                    throw new InvalidDataException(
                        $"Edge viewport mismatch. Expected {width}x{height} CSS at {scale:0.###}x; " +
                        $"received {actualWidth}x{actualHeight} CSS at {actualScale:0.###}x for '{actualUrl}'.");
                }

                return;
            }

            throw new TimeoutException($"Edge did not reach the expected SVG document within {StartupTimeout}.");
        }

        private Task<JsonElement> SendPageCommandAsync(string method, object parameters, TimeSpan? timeout = null)
        {
            string sessionId = _pageSessionId
                ?? throw new InvalidOperationException("The Edge page session is not attached.");
            return SendCommandAsync(method, parameters, sessionId, timeout);
        }

        private async Task<JsonElement> SendCommandAsync(
            string method,
            object parameters,
            string? sessionId = null,
            TimeSpan? timeoutOverride = null)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_socket.State != WebSocketState.Open)
            {
                throw new WebSocketException($"The Edge DevTools websocket is not open ({_socket.State}).");
            }

            int id = Interlocked.Increment(ref _nextCommandId);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion))
            {
                throw new InvalidOperationException("Could not register a DevTools command response.");
            }

            try
            {
                var command = new Dictionary<string, object?>(4)
                {
                    ["id"] = id,
                    ["method"] = method,
                    ["params"] = parameters,
                };
                if (sessionId is not null)
                {
                    command["sessionId"] = sessionId;
                }

                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(command);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_readerCancellation.Token);
                timeout.CancelAfter(timeoutOverride ?? CommandTimeout);
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                JsonElement response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (response.TryGetProperty("error", out JsonElement error))
                {
                    string message = error.TryGetProperty("message", out JsonElement errorMessage)
                        ? errorMessage.GetString() ?? error.ToString()
                        : error.ToString();
                    throw new InvalidOperationException($"Edge DevTools command '{method}' failed: {message}. {DescribeLifecycle()}");
                }

                if (!response.TryGetProperty("result", out JsonElement result))
                {
                    throw new InvalidDataException($"Edge DevTools command '{method}' returned no result.");
                }

                return result.Clone();
            }
            catch (OperationCanceledException ex) when (!_readerCancellation.IsCancellationRequested)
            {
                throw new TimeoutException($"Edge DevTools command '{method}' exceeded {timeoutOverride ?? CommandTimeout}. {DescribeLifecycle()}", ex);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private async Task ReadMessagesAsync()
        {
            byte[] receiveBuffer = new byte[32 * 1024];
            try
            {
                while (!_readerCancellation.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult chunk;
                    do
                    {
                        chunk = await _socket.ReceiveAsync(receiveBuffer, _readerCancellation.Token).ConfigureAwait(false);
                        if (chunk.MessageType == WebSocketMessageType.Close)
                        {
                            throw new WebSocketException("Edge closed the DevTools connection.");
                        }

                        if (chunk.MessageType != WebSocketMessageType.Text || message.Length + chunk.Count > MaximumCdpMessageBytes)
                        {
                            throw new InvalidDataException("Edge sent an unsupported or oversized DevTools message.");
                        }

                        message.Write(receiveBuffer, 0, chunk.Count);
                    }
                    while (!chunk.EndOfMessage);

                    using JsonDocument document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, checked((int)message.Length)));
                    JsonElement root = document.RootElement;
                    if (!root.TryGetProperty("id", out JsonElement responseId) || !responseId.TryGetInt32(out int id))
                    {
                        if (root.TryGetProperty("method", out JsonElement eventMethod) &&
                            eventMethod.GetString() is string eventName &&
                            (eventName.StartsWith("Target.", StringComparison.Ordinal) ||
                             eventName.StartsWith("Inspector.", StringComparison.Ordinal)))
                        {
                            // Keep bounded lifecycle evidence, not document source,
                            // so a detached/crashed target is distinguishable from
                            // a renderer pixel failure or an animation-frame wait.
                            string detail = eventName;
                            if (root.TryGetProperty("params", out JsonElement eventParameters) &&
                                eventParameters.TryGetProperty("reason", out JsonElement reason))
                            {
                                detail += ": " + reason.GetString();
                            }
                            _lifecycleEvents.Enqueue(detail);
                            while (_lifecycleEvents.Count > 8) _lifecycleEvents.TryDequeue(out _);
                        }
                        continue;
                    }

                    if (_pending.TryGetValue(id, out TaskCompletionSource<JsonElement>? completion))
                    {
                        completion.TrySetResult(root.Clone());
                    }
                }
            }
            catch (OperationCanceledException) when (_readerCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                foreach (TaskCompletionSource<JsonElement> pending in _pending.Values)
                {
                    pending.TrySetException(ex);
                }
            }
            finally
            {
                if (_readerCancellation.IsCancellationRequested)
                {
                    var disposed = new ObjectDisposedException(nameof(BrowserSession));
                    foreach (TaskCompletionSource<JsonElement> pending in _pending.Values)
                    {
                        pending.TrySetException(disposed);
                    }
                }
            }
        }

        private string DescribeLifecycle()
        {
            string result = "Recent Edge target events: " + string.Join(", ", _lifecycleEvents.ToArray());
            string logPath = Path.Combine(_directory, "edge.log");
            try
            {
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(Math.Max(0, stream.Length - 8_192), SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                result += " Edge diagnostic tail: " + reader.ReadToEnd();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // Ask the private browser to shut down cleanly, but never let
            // protocol shutdown interfere with deterministic Job Object cleanup.
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                int id = Interlocked.Increment(ref _nextCommandId);
                byte[] close = JsonSerializer.SerializeToUtf8Bytes(new { id, method = "Browser.close", @params = new { } });
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.SendAsync(close, WebSocketMessageType.Text, true, closeTimeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // The browser may already have disconnected or exited.
            }

            _readerCancellation.Cancel();
            try { _socket.Abort(); } catch { }
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            foreach (TaskCompletionSource<JsonElement> pending in _pending.Values)
            {
                pending.TrySetException(new ObjectDisposedException(nameof(BrowserSession)));
            }

            try { _processJob.Dispose(); } catch { }
            try
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            }
            catch
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            }

            _socket.Dispose();
            _readerCancellation.Dispose();
            _httpClient.Dispose();
            _process.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }

        private static async Task<string> WaitForDevToolsEndpointAsync(string profileDirectory, HttpClient httpClient)
        {
            string activePortPath = Path.Combine(profileDirectory, "DevToolsActivePort");
            long deadline = Environment.TickCount64 + (long)StartupTimeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (File.Exists(activePortPath))
                {
                    string[] lines;
                    try { lines = await File.ReadAllLinesAsync(activePortPath).ConfigureAwait(false); }
                    catch (IOException) { lines = Array.Empty<string>(); }
                    if (lines.Length > 0 && int.TryParse(lines[0], out int port) && port is > 0 and <= 65_535)
                    {
                        string versionUrl = $"http://127.0.0.1:{port}/json/version";
                        try
                        {
                            using HttpResponseMessage response = await httpClient.GetAsync(versionUrl).ConfigureAwait(false);
                            if (response.IsSuccessStatusCode)
                            {
                                return versionUrl;
                            }
                        }
                        catch (HttpRequestException)
                        {
                        }
                        catch (TaskCanceledException)
                        {
                        }
                    }
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            throw new TimeoutException($"Edge did not publish DevToolsActivePort within {StartupTimeout}.");
        }

        private static bool IsTransientNavigationError(string message) =>
            message.Contains("execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cannot find context with specified id", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("inspected target navigated or closed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("frame was detached", StringComparison.OrdinalIgnoreCase);

        private static string BuildBrowserArguments(string profileDirectory)
        {
            string[] args =
            [
                "--headless=new",
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
                "--remote-debugging-port=0",
                "--remote-debugging-address=127.0.0.1",
                "--disable-renderer-backgrounding",
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
            ];

            // BrowserProcessJob builds a native command line from this string,
            // so use Windows quoting rather than ProcessStartInfo.ArgumentList.
            return string.Join(' ', args.Select(QuoteWindowsArgument)) + " " +
                   QuoteWindowsArgument("--user-data-dir=" + profileDirectory) + " --enable-logging " +
                   QuoteWindowsArgument("--log-file=" + Path.Combine(Path.GetDirectoryName(profileDirectory)!, "edge.log")) + " about:blank";
        }

        private static string QuoteWindowsArgument(string argument)
        {
            if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
            {
                return argument;
            }

            var builder = new StringBuilder(argument.Length + 2).Append('"');
            int backslashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1).Append('"');
                    backslashes = 0;
                    continue;
                }

                builder.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }

            builder.Append('\\', backslashes * 2).Append('"');
            return builder.ToString();
        }

        private static void ValidatePngDimensions(byte[] png, int cssWidth, int cssHeight, double scale)
        {
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(signature) ||
                !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            {
                throw new InvalidDataException("Edge did not return a valid PNG screenshot.");
            }

            uint actualWidth = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
            uint actualHeight = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
            uint expectedWidth = checked((uint)Math.Ceiling(cssWidth * scale));
            uint expectedHeight = checked((uint)Math.Ceiling(cssHeight * scale));
            if (actualWidth != expectedWidth || actualHeight != expectedHeight)
            {
                throw new InvalidDataException(
                    $"Edge screenshot size was {actualWidth}x{actualHeight}; expected exact physical viewport " +
                    $"{expectedWidth}x{expectedHeight} from {cssWidth}x{cssHeight} CSS at {scale:0.###}x.");
            }
        }
    }
}
