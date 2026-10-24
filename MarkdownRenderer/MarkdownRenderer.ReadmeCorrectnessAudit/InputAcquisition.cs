using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JitHub.Services.CodeViewer;
using JitHub.Services.Markdown;
using MarkdownRenderer;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;

/// <summary>
/// Builds schema-3 <c>correctness-input-v1</c> folders: the frozen README bytes
/// plus every image the document demands, fetched once with the app's URL
/// rules and keyed exactly as the app's offline audit resolver looks them up.
/// Upstream failures are recorded as terminal states, never stored as passes.
/// See docs/markdown-renderer/phase3-correctness-input-design.md.
/// </summary>
internal static partial class InputAcquisition
{
    internal const int SchemaVersion = 3;
    internal const string Kind = "correctness-input-v1";
    // GitHubImageService.MaxImageBytes: the app never accepts a larger image.
    private const long MaximumImageBytes = 32L * 1024 * 1024;
    // The offline resolver's schema-3 per-document unique asset ceiling.
    private const long MaximumUniqueAssetBytesPerDocument = 1024L * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<int> RunAsync(
        Program.FrozenManifest manifest,
        string manifestSha256,
        string objectStoreRoot,
        IReadOnlyList<string> objectPaths,
        string outputRoot,
        MarkdownEngine engine)
    {
        Directory.CreateDirectory(outputRoot);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        using var client = new HttpClient(handler) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("JitHub-MarkdownRenderer-CorrectnessAudit", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        var fetchCache = new ConcurrentDictionary<string, Lazy<Task<FetchResult>>>(StringComparer.Ordinal);
        var summary = new AcquisitionSummary
        {
            SchemaVersion = SchemaVersion,
            Kind = Kind,
            CorpusManifestSha256 = manifestSha256,
            CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        };

        using var gate = new SemaphoreSlim(8);
        var cases = new List<AcquisitionCaseSummary>(manifest.Repositories.Count);
        for (int index = 0; index < manifest.Repositories.Count; index++)
        {
            Program.FrozenRepository repository = manifest.Repositories[index];
            string existingFolder = Path.Combine(outputRoot, $"{repository.Rank:D3}-{Sanitize(repository.FullName)}");
            // Resume: a completed rank folder is summarized, never refetched or
            // overwritten, unless the current demand rules name a source it
            // lacks; a stale folder moves aside intact and the rank is
            // recaptured. Delete a folder to recapture that rank.
            bool exists = File.Exists(Path.Combine(existingFolder, "manifest.json"));
            if (exists && await IsExistingInputStaleAsync(engine, repository, objectPaths[index], existingFolder).ConfigureAwait(false))
            {
                string staleRoot = outputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-stale";
                Directory.CreateDirectory(staleRoot);
                string staleFolder = Path.Combine(staleRoot,
                    $"{Path.GetFileName(existingFolder)}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}");
                Directory.Move(existingFolder, staleFolder);
                Console.WriteLine($"Rank {repository.Rank}: input lacks current image demand; moved to {staleFolder} and recapturing.");
                exists = false;
            }
            AcquisitionCaseSummary result = exists
                ? SummarizeExisting(repository, existingFolder)
                : await AcquireOneAsync(
                    engine, client, gate, fetchCache, repository, objectPaths[index], outputRoot, manifestSha256).ConfigureAwait(false);
            cases.Add(result);
            Console.WriteLine(
                $"[{index + 1}/{manifest.Repositories.Count}] {repository.FullName}: images={result.ImageDemandCount} " +
                $"stored={result.StoredCount} upstream={result.UpstreamUnavailableCount} other={result.OtherTerminalCount}");
        }

        summary.Cases = cases;
        summary.RankCount = cases.Count;
        summary.ImageDemandCount = cases.Sum(static item => item.ImageDemandCount);
        summary.StoredCount = cases.Sum(static item => item.StoredCount);
        summary.UpstreamUnavailableCount = cases.Sum(static item => item.UpstreamUnavailableCount);
        summary.OtherTerminalCount = cases.Sum(static item => item.OtherTerminalCount);
        summary.UniqueFetchCount = fetchCache.Count;
        await WriteJsonAsync(Path.Combine(outputRoot, "acquisition-summary.json"), summary, AcquisitionJsonContext.Default.AcquisitionSummary)
            .ConfigureAwait(false);
        Console.WriteLine(
            $"Acquired {summary.RankCount} ranks: demand={summary.ImageDemandCount} stored={summary.StoredCount} " +
            $"upstream-unavailable={summary.UpstreamUnavailableCount} other-terminal={summary.OtherTerminalCount} unique-fetches={summary.UniqueFetchCount}");
        return 0;
    }

    private static async Task<AcquisitionCaseSummary> AcquireOneAsync(
        MarkdownEngine engine,
        HttpClient client,
        SemaphoreSlim gate,
        ConcurrentDictionary<string, Lazy<Task<FetchResult>>> fetchCache,
        Program.FrozenRepository repository,
        string objectPath,
        string outputRoot,
        string manifestSha256)
    {
        byte[] bytes = await Program.ReadBoundedBytesAsync(objectPath, Program.MaximumReadmeBytes, repository.Readme.ByteSize)
            .ConfigureAwait(false);
        string source = Encoding.UTF8.GetString(bytes);
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source).ConfigureAwait(false);
        string[] ownerAndName = repository.FullName.Split('/', 2);
        string commit = repository.CommitSha.ToLowerInvariant();
        string readmePath = repository.Readme.Path;
        // The app's audit launch passes --branch=<commit>, so the README's
        // context is the commit-pinned blob URL and repository document source.
        var baseUri = new Uri(GitHubCodeUrlBuilder.BuildBlobUrl(ownerAndName[0], ownerAndName[1], commit, readmePath));
        var documentSource = new MarkdownDocumentSource(
            $"repository-file:{repository.FullName}:{commit}:{readmePath}",
            ownerAndName[0],
            ownerAndName[1],
            commit,
            readmePath);

        DocumentInventory inventory = DocumentInventoryBuilder.Create(document);
        List<(string Origin, int Start, int Length, string? Source)> occurrences =
            await CollectImageOccurrencesAsync(engine, document, inventory, source).ConfigureAwait(false);

        var demand = new List<ImageDemand>(occurrences.Count);
        foreach ((string origin, int start, int length, string? authored) in occurrences)
        {
            var entry = new ImageDemand
            {
                Origin = origin,
                SourceStart = start,
                SourceLength = length,
                SourceSha256 = authored is null ? null : Program.HashUtf8(authored),
            };
            demand.Add(entry);
            if (string.IsNullOrWhiteSpace(authored))
            {
                entry.State = "missing-source";
                continue;
            }

            entry.CandidateUrlSha256s = MarkdownAuditImageUrlCandidates.GetSourceUrlHashes(
                authored, baseUri, readmePath, documentSource, repository.FullName, commit).ToList();
            Uri? fetch = SelectFetchUri(authored, baseUri, readmePath, documentSource, out string? state);
            if (fetch is null)
            {
                entry.State = state ?? "unresolvable";
                continue;
            }

            entry.FetchUrl = fetch.AbsoluteUri;
            FetchResult result = await fetchCache
                .GetOrAdd(fetch.AbsoluteUri, url => new Lazy<Task<FetchResult>>(() => FetchAsync(client, gate, new Uri(url))))
                .Value
                .ConfigureAwait(false);
            entry.State = result.State;
            entry.HttpStatus = result.HttpStatus;
            entry.FinalUrl = result.FinalUrl;
            entry.MimeType = result.MimeType;
            entry.AssetSha256 = result.Sha256;
            entry.AssetByteSize = result.Bytes?.LongLength;
        }

        return await WriteCaseAsync(repository, bytes, demand, inventory, outputRoot, manifestSha256, fetchCache).ConfigureAwait(false);
    }

    private static AcquisitionCaseSummary SummarizeExisting(Program.FrozenRepository repository, string folder)
    {
        CorrectnessInputManifest existing = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(folder, "manifest.json")),
            AcquisitionJsonContext.Default.CorrectnessInputManifest)
            ?? throw new InvalidDataException($"Existing correctness input for rank {repository.Rank} is empty.");
        if (!string.Equals(existing.Repository.FullName, repository.FullName, StringComparison.Ordinal) ||
            !string.Equals(existing.Repository.CommitSha, repository.CommitSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Existing correctness input for rank {repository.Rank} names a different repository or commit.");
        }

        return new AcquisitionCaseSummary
        {
            Rank = repository.Rank,
            FullName = repository.FullName,
            Folder = Path.GetFileName(folder),
            ImageDemandCount = existing.ImageDemand.Count,
            StoredCount = existing.ImageDemand.Count(static item => item.State == "stored"),
            UpstreamUnavailableCount = existing.ImageDemand.Count(static item => item.State == "upstream-unavailable"),
            OtherTerminalCount = existing.ImageDemand.Count(static item => item.State is not ("stored" or "upstream-unavailable")),
        };
    }

    /// <summary>Every image source the view may request, in source order of each rule.</summary>
    private static async Task<List<(string Origin, int Start, int Length, string? Source)>> CollectImageOccurrencesAsync(
        MarkdownEngine engine,
        MarkdownRenderer.Document.MarkdownDocument document,
        DocumentInventory inventory,
        string source)
    {
        var occurrences = new List<(string Origin, int Start, int Length, string? Source)>();
        foreach (MarkdownImage image in document.GetImages())
            occurrences.Add(("markdown", image.SourceSpan.Start, image.SourceSpan.Length, image.Source));
        foreach (InventoryHtmlImage image in inventory.HtmlImages)
            occurrences.Add((image.Origin, image.SourceStart, image.SourceLength, image.Src));
        foreach ((int start, int length, string? src) in await GetMarkdownImagesInsideHtmlBlocksAsync(engine, document, source)
            .ConfigureAwait(false))
        {
            occurrences.Add(("html-block-markdown", start, length, src));
        }

        return occurrences;
    }

    /// <summary>
    /// A completed input is stale when the current demand rules name an image
    /// source its demand does not contain (rules added after it was captured).
    /// </summary>
    private static async Task<bool> IsExistingInputStaleAsync(
        MarkdownEngine engine,
        Program.FrozenRepository repository,
        string objectPath,
        string folder)
    {
        CorrectnessInputManifest existing = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(folder, "manifest.json")),
            AcquisitionJsonContext.Default.CorrectnessInputManifest)
            ?? throw new InvalidDataException($"Existing correctness input for rank {repository.Rank} is empty.");
        var demanded = new HashSet<string>(
            existing.ImageDemand.Select(static item => item.SourceSha256).OfType<string>(),
            StringComparer.Ordinal);
        byte[] bytes = await Program.ReadBoundedBytesAsync(objectPath, Program.MaximumReadmeBytes, repository.Readme.ByteSize)
            .ConfigureAwait(false);
        string source = Encoding.UTF8.GetString(bytes);
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source).ConfigureAwait(false);
        DocumentInventory inventory = DocumentInventoryBuilder.Create(document);
        foreach ((_, _, _, string? authored) in await CollectImageOccurrencesAsync(engine, document, inventory, source)
            .ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(authored) && !demanded.Contains(Program.HashUtf8(authored)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Ranks 34 and 176: GitHub, and the view, render Markdown inside inert
    /// HTML containers (<c>&lt;div align="center"&gt;</c> badge and sponsor
    /// groups), which CommonMark keeps as raw HTML-block text. Reparse each
    /// HTML block's text with its tags removed, plus the document's link
    /// reference definitions so <c>[![1.1]][1]</c> resolves, and demand the
    /// images it contains. A demanded image the view does not request is
    /// harmless; a missing one makes a correct image look like a failure.
    /// </summary>
    internal static async Task<List<(int Start, int Length, string? Source)>> GetMarkdownImagesInsideHtmlBlocksAsync(
        MarkdownEngine engine,
        MarkdownRenderer.Document.MarkdownDocument document,
        string source)
    {
        var result = new List<(int, int, string?)>();
        Markdig.Syntax.MarkdownDocument? parsed = document.ParsedDocument;
        if (parsed is null)
            return result;

        var definitions = new StringBuilder();
        foreach (Markdig.Syntax.LinkReferenceDefinition definition in
                 Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.LinkReferenceDefinition>(parsed))
        {
            if (definition.Span.Start >= 0 && definition.Span.End < source.Length && definition.Span.Length > 0)
                definitions.Append('\n').Append(source, definition.Span.Start, definition.Span.Length).Append('\n');
        }

        foreach (Markdig.Syntax.HtmlBlock html in
                 Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.HtmlBlock>(parsed))
        {
            if (html.Span.Start < 0 || html.Span.End >= source.Length || html.Span.Length <= 0)
                continue;
            string text = source.Substring(html.Span.Start, html.Span.Length);
            if (!text.Contains("](", StringComparison.Ordinal) && !text.Contains("][", StringComparison.Ordinal))
                continue;
            // Tags become line breaks so each container's text reparses as
            // its own Markdown blocks.
            string markdown = HtmlTag().Replace(text, "\n\n") + "\n\n" + definitions;
            MarkdownRenderer.Document.MarkdownDocument fragment = await engine.ParseAsync(markdown).ConfigureAwait(false);
            foreach (MarkdownImage image in fragment.GetImages())
                result.Add((html.Span.Start, html.Span.Length, image.Source));
        }

        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<[^>\n]*>", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex HtmlTag();

    /// <summary>
    /// Chooses the one URL the app would fetch: a GitHub repository image at
    /// its raw URL and ref, otherwise the authored absolute URL with http
    /// upgraded to https as <c>MarkdownImageSourcePolicy</c> does. Data and
    /// non-http(s) sources never reach a resolver.
    /// </summary>
    internal static Uri? SelectFetchUri(
        string authored,
        Uri baseUri,
        string readmePath,
        MarkdownDocumentSource documentSource,
        out string? terminalState)
    {
        terminalState = null;
        string trimmed = authored.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            terminalState = "data-uri";
            return null;
        }

        if (GitHubMarkdownImageUrlResolver.TryResolve(trimmed, documentSource, out GitHubMarkdownImageReference reference) ||
            GitHubMarkdownImageUrlResolver.TryResolve(trimmed, baseUri, readmePath, out reference))
        {
            return GitHubMarkdownImageUrlResolver.CreateRawUri(reference);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? absolute))
        {
            terminalState = "unresolvable";
            return null;
        }

        if (absolute.Scheme == Uri.UriSchemeHttp)
            return new UriBuilder(absolute) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        if (absolute.Scheme == Uri.UriSchemeHttps)
            return absolute;

        terminalState = "unsupported-scheme";
        return null;
    }

    private static async Task<FetchResult> FetchAsync(HttpClient client, SemaphoreSlim gate, Uri uri)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Uri current = uri;
            for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using HttpResponseMessage response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (current.Scheme != Uri.UriSchemeHttps)
                        return new FetchResult { State = "insecure-redirect", HttpStatus = status, FinalUrl = current.AbsoluteUri };
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return new FetchResult { State = "upstream-unavailable", HttpStatus = status, FinalUrl = current.AbsoluteUri };

                if (response.Content.Headers.ContentLength is > MaximumImageBytes)
                    return new FetchResult { State = "too-large", HttpStatus = status, FinalUrl = current.AbsoluteUri };

                await using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaximumImageBytes)
                        return new FetchResult { State = "too-large", HttpStatus = status, FinalUrl = current.AbsoluteUri };
                    buffer.Write(chunk, 0, read);
                }

                byte[] bytes = buffer.ToArray();
                if (bytes.Length == 0)
                    return new FetchResult { State = "empty-response", HttpStatus = status, FinalUrl = current.AbsoluteUri };

                string mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "application/octet-stream";
                if (!mime.StartsWith("image/", StringComparison.Ordinal) && mime != "application/octet-stream")
                    return new FetchResult { State = "not-an-image", HttpStatus = status, FinalUrl = current.AbsoluteUri, MimeType = mime };

                return new FetchResult
                {
                    State = "stored",
                    HttpStatus = status,
                    FinalUrl = current.AbsoluteUri,
                    MimeType = mime,
                    Bytes = bytes,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                };
            }

            return new FetchResult { State = "too-many-redirects", FinalUrl = current.AbsoluteUri };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return new FetchResult { State = "upstream-unavailable", Error = exception.GetType().Name };
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<AcquisitionCaseSummary> WriteCaseAsync(
        Program.FrozenRepository repository,
        byte[] readmeBytes,
        List<ImageDemand> demand,
        DocumentInventory inventory,
        string outputRoot,
        string manifestSha256,
        ConcurrentDictionary<string, Lazy<Task<FetchResult>>> fetchCache)
    {
        string folderName = $"{repository.Rank:D3}-{Sanitize(repository.FullName)}";
        string folder = Path.Combine(outputRoot, folderName);
        if (Directory.Exists(folder))
            throw new IOException($"Refusing to overwrite an existing correctness input: {folder}");
        string assets = Path.Combine(folder, "assets");
        Directory.CreateDirectory(assets);
        await File.WriteAllBytesAsync(Path.Combine(folder, "readme.md"), readmeBytes).ConfigureAwait(false);

        var assetEntries = new Dictionary<string, CorpusAsset>(StringComparer.Ordinal);
        var written = new HashSet<string>(StringComparer.Ordinal);
        long uniqueBytes = 0;
        foreach (ImageDemand entry in demand)
        {
            if (entry.State != "stored" || entry.AssetSha256 is null || entry.FetchUrl is null)
                continue;

            FetchResult fetched = await fetchCache[entry.FetchUrl].Value.ConfigureAwait(false);
            if (!written.Contains(entry.AssetSha256))
            {
                if (uniqueBytes + fetched.Bytes!.LongLength > MaximumUniqueAssetBytesPerDocument)
                {
                    entry.State = "resource-limit";
                    continue;
                }
                await File.WriteAllBytesAsync(Path.Combine(assets, entry.AssetSha256), fetched.Bytes).ConfigureAwait(false);
                written.Add(entry.AssetSha256);
                uniqueBytes += fetched.Bytes.LongLength;
            }

            foreach (string urlSha256 in entry.CandidateUrlSha256s)
            {
                if (assetEntries.TryGetValue(urlSha256, out CorpusAsset? existing))
                {
                    if (existing.Sha256 != entry.AssetSha256)
                        throw new InvalidDataException($"{repository.FullName}: one URL key maps to two different assets.");
                    continue;
                }
                assetEntries[urlSha256] = new CorpusAsset
                {
                    UrlSha256 = urlSha256,
                    Sha256 = entry.AssetSha256,
                    ByteSize = fetched.Bytes!.LongLength,
                    MimeType = fetched.MimeType!,
                };
            }
        }

        var corpus = new CorrectnessInputManifest
        {
            SchemaVersion = SchemaVersion,
            Kind = Kind,
            Complete = true,
            Repository = new CorpusRepository
            {
                FullName = repository.FullName,
                CommitSha = repository.CommitSha.ToLowerInvariant(),
                ReadmePath = repository.Readme.Path,
                ReadmeGitBlobSha1 = repository.Readme.Sha.ToLowerInvariant(),
                Rank = repository.Rank,
                RepositoryId = repository.Id,
                CorpusManifestSha256 = manifestSha256,
            },
            Readme = new CorpusReadme
            {
                File = "readme.md",
                ByteSize = readmeBytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(readmeBytes)).ToLowerInvariant(),
            },
            Assets = assetEntries.Values.OrderBy(static asset => asset.UrlSha256, StringComparer.Ordinal).ToList(),
            ImageDemand = demand,
            Acquisition = new CorpusAcquisition
            {
                Tool = "MarkdownRenderer.ReadmeCorrectnessAudit --acquire-inputs",
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                HostPolicy = "GitHub repository images at their raw URL and ref; other absolute http(s) URLs as authored with http upgraded to https; data URIs are not fetched.",
                UnrecognizedInlineTypes = inventory.UnrecognizedInlineTypes,
            },
        };
        await WriteJsonAsync(Path.Combine(folder, "manifest.json"), corpus, AcquisitionJsonContext.Default.CorrectnessInputManifest)
            .ConfigureAwait(false);

        return new AcquisitionCaseSummary
        {
            Rank = repository.Rank,
            FullName = repository.FullName,
            Folder = folderName,
            ImageDemandCount = demand.Count,
            StoredCount = demand.Count(static item => item.State == "stored"),
            UpstreamUnavailableCount = demand.Count(static item => item.State == "upstream-unavailable"),
            OtherTerminalCount = demand.Count(static item => item.State is not ("stored" or "upstream-unavailable")),
        };
    }

    /// <summary>
    /// Writes each rank's expected-content inventory beside its correctness
    /// input and binds it into manifest.json by size and SHA-256, so the
    /// in-app probe verifies it like every other input file.
    /// </summary>
    internal static async Task<int> AttachInventoryAsync(
        Program.FrozenManifest manifest,
        IReadOnlyList<string> objectPaths,
        string inputRoot,
        MarkdownEngine engine)
    {
        int attached = 0;
        for (int index = 0; index < manifest.Repositories.Count; index++)
        {
            Program.FrozenRepository repository = manifest.Repositories[index];
            string folder = Path.Combine(inputRoot, $"{repository.Rank:D3}-{Sanitize(repository.FullName)}");
            string manifestPath = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException($"Missing correctness input for rank {repository.Rank}.", manifestPath);

            byte[] bytes = await Program.ReadBoundedBytesAsync(objectPaths[index], Program.MaximumReadmeBytes, repository.Readme.ByteSize)
                .ConfigureAwait(false);
            byte[] stored = await File.ReadAllBytesAsync(Path.Combine(folder, "readme.md")).ConfigureAwait(false);
            if (!bytes.AsSpan().SequenceEqual(stored))
                throw new InvalidDataException($"Rank {repository.Rank} correctness input README differs from the frozen blob.");

            MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(Encoding.UTF8.GetString(bytes)).ConfigureAwait(false);
            var expected = new ExpectedContent
            {
                Inventory = DocumentInventoryBuilder.Create(document),
                HeadingCount = document.GetHeadings().Count,
                LinkCount = document.GetLinks().Count,
                MarkdownImageCount = document.GetImages().Count,
                CodeBlockCount = document.GetCodeBlocks().Count(static block =>
                    !string.Equals(block.Language, "mermaid", StringComparison.OrdinalIgnoreCase)),
                MermaidDiagramCount = document.GetCodeBlocks().Count(static block =>
                    string.Equals(block.Language, "mermaid", StringComparison.OrdinalIgnoreCase)),
                Diagnostics = document.Diagnostics.Select(static diagnostic => new Program.DiagnosticResult
                {
                    Code = diagnostic.Code,
                    Severity = diagnostic.Severity.ToString(),
                    SourceStart = diagnostic.SourceSpan.Start,
                    SourceLength = diagnostic.SourceSpan.Length,
                    Message = diagnostic.Message,
                }).ToList(),
            };

            string inventoryPath = Path.Combine(folder, "inventory.json");
            if (File.Exists(inventoryPath))
                File.Delete(inventoryPath);
            await WriteJsonAsync(inventoryPath, expected, AcquisitionJsonContext.Default.ExpectedContent).ConfigureAwait(false);
            byte[] inventoryBytes = await File.ReadAllBytesAsync(inventoryPath).ConfigureAwait(false);

            var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false))!.AsObject();
            node["inventory"] = new System.Text.Json.Nodes.JsonObject
            {
                ["file"] = "inventory.json",
                ["byteSize"] = inventoryBytes.LongLength,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(inventoryBytes)).ToLowerInvariant(),
            };
            string temporary = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
            File.Move(temporary, manifestPath, overwrite: true);
            attached++;
        }

        Console.WriteLine($"Attached expected-content inventories to {attached} correctness inputs.");
        return attached == manifest.Repositories.Count ? 0 : 1;
    }

    // Matches ReadmeAuditProbe.Sanitize so the probe finds the rank folder.
    internal static string Sanitize(string value) => string.Concat(
        value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));

    private static async Task WriteJsonAsync<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (FileStream stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, value, typeInfo).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    internal sealed class FetchResult
    {
        public string State { get; set; } = "";
        public int? HttpStatus { get; set; }
        public string? FinalUrl { get; set; }
        public string? MimeType { get; set; }
        public string? Sha256 { get; set; }
        public string? Error { get; set; }
        [JsonIgnore] public byte[]? Bytes { get; set; }
    }
}

internal sealed class CorrectnessInputManifest
{
    public int SchemaVersion { get; set; }
    public string Kind { get; set; } = "";
    public bool Complete { get; set; }
    public CorpusRepository Repository { get; set; } = new();
    public CorpusReadme Readme { get; set; } = new();
    public List<CorpusAsset> Assets { get; set; } = [];
    public List<ImageDemand> ImageDemand { get; set; } = [];
    public CorpusAcquisition Acquisition { get; set; } = new();
}

internal sealed class CorpusRepository
{
    public string FullName { get; set; } = "";
    public string CommitSha { get; set; } = "";
    public string ReadmePath { get; set; } = "";
    public string ReadmeGitBlobSha1 { get; set; } = "";
    public int Rank { get; set; }
    public long RepositoryId { get; set; }
    public string CorpusManifestSha256 { get; set; } = "";
}

internal sealed class CorpusReadme
{
    public string File { get; set; } = "";
    public long ByteSize { get; set; }
    public string Sha256 { get; set; } = "";
}

internal sealed class CorpusAsset
{
    public string UrlSha256 { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long ByteSize { get; set; }
    public string MimeType { get; set; } = "";
}

internal sealed class ImageDemand
{
    public string Origin { get; set; } = "";
    public int SourceStart { get; set; }
    public int SourceLength { get; set; }
    public string? SourceSha256 { get; set; }
    public List<string> CandidateUrlSha256s { get; set; } = [];
    public string? FetchUrl { get; set; }
    public string State { get; set; } = "";
    public int? HttpStatus { get; set; }
    public string? FinalUrl { get; set; }
    public string? MimeType { get; set; }
    public string? AssetSha256 { get; set; }
    public long? AssetByteSize { get; set; }
}

internal sealed class CorpusAcquisition
{
    public string Tool { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string HostPolicy { get; set; } = "";
    public List<string> UnrecognizedInlineTypes { get; set; } = [];
}

/// <summary>What the native view was asked to show, from the parsed tree.</summary>
internal sealed class ExpectedContent
{
    public DocumentInventory Inventory { get; set; } = new();
    public int HeadingCount { get; set; }
    public int LinkCount { get; set; }
    public int MarkdownImageCount { get; set; }
    public int CodeBlockCount { get; set; }
    public int MermaidDiagramCount { get; set; }
    public List<Program.DiagnosticResult> Diagnostics { get; set; } = [];
}

internal sealed class AcquisitionCaseSummary
{
    public int Rank { get; set; }
    public string FullName { get; set; } = "";
    public string Folder { get; set; } = "";
    public int ImageDemandCount { get; set; }
    public int StoredCount { get; set; }
    public int UpstreamUnavailableCount { get; set; }
    public int OtherTerminalCount { get; set; }
}

internal sealed class AcquisitionSummary
{
    public int SchemaVersion { get; set; }
    public string Kind { get; set; } = "";
    public string CorpusManifestSha256 { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public int RankCount { get; set; }
    public int ImageDemandCount { get; set; }
    public int StoredCount { get; set; }
    public int UpstreamUnavailableCount { get; set; }
    public int OtherTerminalCount { get; set; }
    public int UniqueFetchCount { get; set; }
    public List<AcquisitionCaseSummary> Cases { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(CorrectnessInputManifest))]
[JsonSerializable(typeof(AcquisitionSummary))]
[JsonSerializable(typeof(ExpectedContent))]
internal partial class AcquisitionJsonContext : JsonSerializerContext
{
}
