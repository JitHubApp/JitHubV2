using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using Xunit;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceAdmissionDoubles;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// HTTP handlers and contents shared by the <c>GitHubImageService</c> test classes.
/// </summary>
internal static class GitHubImageServiceHttpDoubles
{
    internal static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    internal sealed class CountingHandler(byte[] bytes, TimeSpan? delay = null) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (delay is TimeSpan value)
            {
                await Task.Delay(value, cancellationToken);
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    internal sealed class DisposalTrackingHandler : HttpMessageHandler
    {
        private int _disposed;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("This handler is used only to verify disposal.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Interlocked.Exchange(ref _disposed, 1);
            base.Dispose(disposing);
        }
    }

    internal sealed class RevalidationHandler(byte[] bytes) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public bool SawConditionalRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber > 1)
            {
                SawConditionalRequest = request.Headers.IfNoneMatch.Count > 0;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
            response.Headers.ETag = new("\"avatar-v1\"");
            return Task.FromResult(response);
        }
    }

    internal sealed class NonImageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            StringContent content = new("<html>not an image</html>");
            content.Headers.ContentType = new("text/html");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class CancellationAwareHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancellation test request unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    internal sealed class FailingRefreshHandler(byte[] bytes) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) > 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class TransientStatusThenImageHandler(
        byte[] bytes,
        int failuresBeforeSuccess = 1) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) <= failuresBeforeSuccess)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class TransientExceptionThenImageHandler(byte[] bytes) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Simulated HTTP/2 stream reset."));
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class AlwaysStatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    internal sealed class ReplacingImageHandler(byte[] first, byte[] replacement) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[] bytes = Interlocked.Increment(ref _requestCount) == 1 ? first : replacement;
            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class RedirectHandler(string destination) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(destination);
            return Task.FromResult(response);
        }
    }

    internal sealed class LoopingRedirectHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int requestNumber = Interlocked.Increment(ref _requestCount);
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri($"https://images.example.com/loop-{requestNumber}.png");
            return Task.FromResult(response);
        }
    }

    internal sealed class OversizedImageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ByteArrayContent content = new(PngBytes);
            content.Headers.ContentType = new("image/png");
            content.Headers.ContentLength = 32L * 1024 * 1024 + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class RawImageHandler(string contentType, byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class SequencedSvgHandler(params byte[][] payloads) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref _requestCount) - 1;
            ByteArrayContent content = new(payloads[Math.Min(index, payloads.Length - 1)]);
            content.Headers.ContentType = new("image/svg+xml");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class UnknownLengthImageHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            UnknownLengthContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    internal sealed class AdmissionAwareHandler(
        byte[] bytes,
        bool knownLength,
        Action onStreamCreated) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new AdmissionAwareContent(bytes, knownLength, onStreamCreated),
            });
    }

    internal sealed class AdmissionAwareContent(
        byte[] bytes,
        bool knownLength,
        Action onStreamCreated) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = knownLength ? bytes.Length : 0;
            return knownLength;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            onStreamCreated();
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    internal sealed class GatedImageHandler(
        byte[] bytes,
        GatedImageAdmission admission,
        bool knownLength = true) : HttpMessageHandler
    {
        private int _requestCount;
        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new GatedImageContent(bytes, admission, knownLength),
            });
        }
    }

    internal sealed class GatedImageContent(
        byte[] bytes,
        GatedImageAdmission admission,
        bool knownLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = knownLength ? bytes.Length : 0;
            return knownLength;
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            // Fill the configured admission before any body can finish, so
            // concurrent readers exercise the budget and lease handoff.
            await admission.WaitUntilFullAsync();
            return new MemoryStream(bytes, writable: false);
        }
    }

    internal sealed class DeclaredLengthImageHandler(int declaredLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ByteArrayContent content = new(PngBytes);
            content.Headers.ContentType = new("image/png");
            content.Headers.ContentLength = declaredLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class RedirectThenImageHandler(
        string destination,
        byte[] bytes,
        string contentType = "application/octet-stream") : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber == 1)
            {
                HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri(destination);
                return Task.FromResult(redirect);
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    internal sealed class RenderedReadmeHandler(string html) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string Accept { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            Accept = request.Headers.Accept.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
            });
        }
    }

    internal sealed class AuthPolicyRenderedReadmeHandler(string html) : HttpMessageHandler
    {
        public List<string?> AuthorizationSchemes { get; } = [];
        public List<string> AcceptValues { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme);
            AcceptValues.Add(request.Headers.Accept.ToString());
            if (AuthorizationSchemes.Count == 1)
            {
                var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        "{\"message\":\"Resource not accessible by integration\"}",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
                forbidden.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "4999");
                return Task.FromResult(forbidden);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
            });
        }
    }
}
