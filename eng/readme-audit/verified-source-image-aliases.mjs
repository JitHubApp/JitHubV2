import { createHash } from "node:crypto";

const GIT_SHA1_PATTERN = /^[0-9a-f]{40}$/iu;
const SHA256_PATTERN = /^[0-9a-f]{64}$/u;
const MAX_TRACKED_SOURCES = 15_000;
const MAX_SOURCE_URL_BYTES = 16 * 1024;
const MAX_REDIRECTS = 5;
// Alias discovery is an optional lookup repair, not part of image acquisition.
// Keep its total cost bounded per README so a 500-repository audit cannot turn
// into an unbounded second download pass.
const MAX_SOURCE_FETCHES_PER_CASE = 16;
const MAX_SOURCE_FETCH_DURATION_MS = 10_000;
const MAX_SOURCE_IMAGE_BYTES_PER_RESOURCE = 16 * 1024 * 1024;
const MAX_SOURCE_IMAGE_BYTES_PER_CASE = 32 * 1024 * 1024;
const GITHUB_IMAGE_HOSTS = new Set([
  "github.com",
  "githubusercontent.com",
]);
const GITHUB_PRODUCTION_ASSET_HOST = "github-production-user-asset-6210df.s3.amazonaws.com";

/**
 * Captures source-image bytes only from an immutable repository commit or a
 * credential-free GitHub URL. A source alias is emitted only after the body is
 * verified to be a supported image and content-addressed by its exact bytes.
 * A source URL is added only when the fetched source body has the exact SHA-256
 * of a browser-captured payload. This preserves same-byte parity: source capture
 * can add lookup aliases, but can never add a raster the browser oracle did not
 * capture.
 */
export async function captureVerifiedMarkdownImageAliases({
  sources,
  repository,
  commitSha,
  readmePath,
  assets,
  fetchImpl = fetch,
  maxAssetBytes = 64 * 1024 * 1024,
  maxAggregateAssetBytes = 256 * 1024 * 1024,
  maxLookupEntries = 15_000,
}) {
  if (!Array.isArray(sources) || sources.length > MAX_TRACKED_SOURCES ||
      !Array.isArray(assets) || typeof fetchImpl !== "function" ||
      !Number.isSafeInteger(maxAssetBytes) || maxAssetBytes <= 0 ||
      !Number.isSafeInteger(maxAggregateAssetBytes) || maxAggregateAssetBytes <= 0 ||
      !Number.isSafeInteger(maxLookupEntries) || maxLookupEntries <= 0) {
    throw new Error("Pinned Markdown image capture is malformed or exceeds its bounded source count.");
  }

  const identity = validatePinnedIdentity(repository, commitSha, readmePath);
  const assetByHash = new Map();
  const assetByUrlHash = new Map();
  let aggregateBytes = 0;
  for (const sourceAsset of assets) {
    if (!sourceAsset || !SHA256_PATTERN.test(sourceAsset.sha256 || "") ||
        !Buffer.isBuffer(sourceAsset.bytes) || sourceAsset.bytes.length === 0 ||
        sourceAsset.bytes.length > maxAssetBytes) {
      throw new Error("Existing same-byte image asset is malformed or exceeds its per-image byte limit.");
    }
    const actualHash = sha256(sourceAsset.bytes);
    if (actualHash !== sourceAsset.sha256.toLowerCase()) {
      throw new Error("Existing same-byte image asset does not match its content address.");
    }
    const asset = assetByHash.get(actualHash) ?? {
      sha256: actualHash,
      bytes: sourceAsset.bytes,
      mimeType: sourceAsset.mimeType,
      urlHashes: new Set(),
    };
    const mimeType = sniffImageMime(sourceAsset.bytes);
    if (!mimeType || (sourceAsset.mimeType && sourceAsset.mimeType !== mimeType)) {
      throw new Error("Existing same-byte image MIME does not match its captured payload signature.");
    }
    asset.mimeType = mimeType;
    for (const urlHash of sourceAsset.urlHashes ?? []) {
      if (!SHA256_PATTERN.test(urlHash || "")) {
        throw new Error("Existing same-byte image URL alias is malformed.");
      }
      bindUrlHash(assetByUrlHash, asset, urlHash.toLowerCase());
      asset.urlHashes.add(urlHash.toLowerCase());
    }
    assetByHash.set(actualHash, asset);
  }
  for (const asset of assetByHash.values()) aggregateBytes += asset.bytes.length;
  if (aggregateBytes > maxAggregateAssetBytes) {
    throw new Error("Existing same-byte assets exceed the aggregate image byte limit.");
  }

  const processedSources = new Set();
  let reusedCount = 0;
  let fetchedCount = 0;
  let skippedCount = 0;
  let byteMismatchCount = 0;
  let sourceFetchAttemptCount = 0;
  let sourceFetchBytes = 0;
  let sourceFetchBudgetExhausted = false;
  const sourceFetchDeadline = AbortSignal.timeout(MAX_SOURCE_FETCH_DURATION_MS);
  for (const source of sources) {
    if (typeof source !== "string" || source.length === 0 ||
        Buffer.byteLength(source, "utf8") > MAX_SOURCE_URL_BYTES ||
        /^(?:data:|javascript:)/iu.test(source.trim())) {
      skippedCount++;
      continue;
    }

    const resolved = resolveImageSource(source, identity);
    if (!resolved) {
      skippedCount++;
      continue;
    }
    const sourceKey = resolved.fetchUrl;
    if (processedSources.has(sourceKey)) continue;
    processedSources.add(sourceKey);

    const aliasHashes = new Set(resolved.aliasUrls.map(url => urlHash(url)));
    if (aliasHashes.size === 0) {
      skippedCount++;
      continue;
    }

    const reusedAssets = new Set();
    for (const hash of aliasHashes) {
      const existing = assetByUrlHash.get(hash);
      if (existing) reusedAssets.add(existing);
    }
    if (reusedAssets.size > 1) {
      throw new Error("One pinned Markdown image URL maps to conflicting captured payloads.");
    }

    let asset = reusedAssets.values().next().value;
    if (asset) {
      reusedCount++;
    } else {
      if (sourceFetchAttemptCount >= MAX_SOURCE_FETCHES_PER_CASE ||
          sourceFetchDeadline.aborted || sourceFetchBytes >= MAX_SOURCE_IMAGE_BYTES_PER_CASE) {
        sourceFetchBudgetExhausted = true;
        skippedCount++;
        continue;
      }
      sourceFetchAttemptCount++;
      const remainingCaseBytes = MAX_SOURCE_IMAGE_BYTES_PER_CASE - sourceFetchBytes;
      const sourceAssetLimit = Math.min(
        maxAssetBytes,
        MAX_SOURCE_IMAGE_BYTES_PER_RESOURCE,
        remainingCaseBytes,
      );
      let fetchResult;
      try {
        fetchResult = await fetchVerifiedImage(resolved.fetchUrl, {
          fetchImpl,
          maxAssetBytes: sourceAssetLimit,
          signal: sourceFetchDeadline,
          onBytesRead: bytesRead => { sourceFetchBytes += bytesRead; },
          allowGitHubProductionAssetRedirect: isGitHubAssetOrigin(resolved.fetchUrl),
        });
      } catch {
        // Source enrichment is best effort. The strict replay gate still
        // reports any URL that cannot be served from exact captured bytes.
        skippedCount++;
        if (sourceFetchDeadline.aborted) sourceFetchBudgetExhausted = true;
        continue;
      }
      const captured = fetchResult?.image;
      if (!captured) {
        skippedCount++;
        continue;
      }
      fetchedCount++;
      asset = assetByHash.get(captured.sha256);
      if (!asset) {
        // The source and browser render selected different bytes. Do not turn
        // this alias into an unobserved asset; the strict image-completeness
        // gate must expose the parity mismatch.
        byteMismatchCount++;
        skippedCount++;
        continue;
      }
      if (asset.mimeType !== captured.mimeType || !asset.bytes.equals(captured.bytes)) {
        throw new Error("Content-addressed source image conflicts with an existing captured payload.");
      }
    }

    for (const hash of aliasHashes) {
      bindUrlHash(assetByUrlHash, asset, hash);
      asset.urlHashes.add(hash);
    }
    if (assetByUrlHash.size > maxLookupEntries) {
      throw new Error("Pinned Markdown image aliases exceed the bounded same-byte URL lookup count.");
    }
  }

  return {
    assets: [...assetByHash.values()]
      .map(asset => ({ ...asset, urlHashes: [...asset.urlHashes].sort() }))
      .sort((left, right) => left.sha256 < right.sha256 ? -1 : left.sha256 > right.sha256 ? 1 : 0),
    reusedCount,
    fetchedCount,
    skippedCount,
    byteMismatchCount,
    sourceFetchAttemptCount,
    sourceFetchBytes,
    sourceFetchBudgetExhausted,
    aggregateBytes,
  };
}

function validatePinnedIdentity(repository, commitSha, readmePath) {
  const fullName = typeof repository?.fullName === "string" ? repository.fullName : "";
  const parts = fullName.split("/");
  if (parts.length !== 2 || parts.some(part => !/^[A-Za-z0-9_.-]+$/u.test(part)) ||
      !GIT_SHA1_PATTERN.test(commitSha || "") || typeof readmePath !== "string" ||
      readmePath.length === 0 || readmePath.length > MAX_SOURCE_URL_BYTES ||
      readmePath.includes("\\") || readmePath.includes("\0")) {
    throw new Error("Pinned Markdown image aliases require a validated repository commit and README path.");
  }
  const pathParts = readmePath.split("/");
  if (pathParts.some(part => part.length === 0 || part === "." || part === "..")) {
    throw new Error("Pinned Markdown README path is not a normalized repository path.");
  }
  const [owner, repo] = parts;
  const prefix = `/${escapeSegment(owner)}/${escapeSegment(repo)}/${commitSha.toLowerCase()}/`;
  const encodedReadmePath = pathParts.map(escapeSegment).join("/");
  return {
    owner,
    repo,
    commitSha: commitSha.toLowerCase(),
    readmePath,
    rawReadmeUrl: `https://raw.githubusercontent.com${prefix}${encodedReadmePath}`,
    rawPrefix: prefix.toLowerCase(),
  };
}

function resolveImageSource(source, identity) {
  const value = source.trim();
  if (value.startsWith("//")) return null;

  let absolute;
  try {
    absolute = new URL(value);
  } catch {
    absolute = null;
  }
  if (absolute) {
    if (!isAllowedGitHubUrl(absolute)) return null;
    const canonical = normalizeGitHubUrl(absolute.href);
    return canonical ? { fetchUrl: canonical, aliasUrls: [canonical] } : null;
  }

  let repositoryPath = value.split(/[?#]/u, 1)[0].replaceAll("\\", "/");
  if (!repositoryPath) return null;
  try {
    repositoryPath = decodeURIComponent(repositoryPath);
  } catch {
    return null;
  }
  const rooted = repositoryPath.startsWith("/");
  const initialParts = rooted
    ? []
    : identity.readmePath.split("/").slice(0, -1);
  for (const part of repositoryPath.split("/")) {
    if (!part || part === ".") continue;
    if (part === "..") {
      if (initialParts.length === 0) return null;
      initialParts.pop();
      continue;
    }
    if (part.includes("/") || part.includes("\\") || part.includes("\0")) return null;
    initialParts.push(part);
  }
  if (initialParts.length === 0) return null;
  const encodedPath = initialParts.map(escapeSegment).join("/");
  const rawUrl = `https://raw.githubusercontent.com/${escapeSegment(identity.owner)}/${escapeSegment(identity.repo)}/${identity.commitSha}/${encodedPath}`;
  return {
    fetchUrl: rawUrl,
    // The native resolver's expected-commit candidate is this exact raw URL.
    // Keep one lookup entry per source URL so alias expansion does not inflate
    // the manifest's per-resource byte accounting.
    aliasUrls: [rawUrl],
  };
}

async function fetchVerifiedImage(url, {
  fetchImpl,
  maxAssetBytes,
  signal,
  onBytesRead,
  allowGitHubProductionAssetRedirect = false,
}) {
  let currentUrl = normalizeGitHubUrl(url);
  if (!currentUrl) return null;
  const visited = new Set();
  let sourceUrl = currentUrl;
  let bytesRead = 0;
  for (let redirects = 0; redirects <= MAX_REDIRECTS; redirects++) {
    if (visited.has(currentUrl)) throw new Error("Pinned GitHub image source contains a redirect loop.");
    visited.add(currentUrl);
    const response = await fetchImpl(currentUrl, {
      headers: { "user-agent": "JitHub-Readme-Same-Byte-Capture" },
      redirect: "manual",
      signal,
    });
    if (response.status >= 300 && response.status < 400) {
      const location = response.headers.get("location");
      await response.body?.cancel();
      if (!location || redirects === MAX_REDIRECTS) return null;
      let redirected;
      try {
        redirected = new URL(location, currentUrl);
      } catch {
        return null;
      }
      const normalizedGitHubUrl = normalizeGitHubUrl(redirected.href);
      if (normalizedGitHubUrl) {
        currentUrl = normalizedGitHubUrl;
        sourceUrl = currentUrl;
        continue;
      }
      if (!allowGitHubProductionAssetRedirect ||
          !isSignedGitHubProductionAssetRedirect(redirected, sourceUrl)) return null;
      currentUrl = redirected.href;
      continue;
    }
    if (!response.ok || !response.body) return null;
    if (response.url) {
      const observedUrl = normalizeGitHubUrl(response.url) ||
        (allowGitHubProductionAssetRedirect &&
         isSignedGitHubProductionAssetRedirect(new URL(response.url), sourceUrl)
          ? new URL(response.url).href
          : "");
      if (!observedUrl || observedUrl !== currentUrl) {
        await response.body.cancel();
        throw new Error("Pinned GitHub image response URL differs from its validated request URL.");
      }
    }
    const bytes = await readBoundedResponse(response, maxAssetBytes, onBytesRead);
    bytesRead += bytes.length;
    const mimeType = sniffImageMime(bytes);
    if (!mimeType) return { image: null, bytesRead };
    return {
      image: {
        bytes,
        mimeType,
        sha256: sha256(bytes),
      },
      bytesRead,
    };
  }
  return { image: null, bytesRead };
}

async function readBoundedResponse(response, maximumBytes, onBytesRead = null) {
  const contentLength = Number(response.headers.get("content-length"));
  if (Number.isSafeInteger(contentLength) && contentLength > maximumBytes) {
    await response.body?.cancel();
    throw new Error("Pinned GitHub image response exceeds its per-image byte limit.");
  }
  const chunks = [];
  let totalBytes = 0;
  for await (const chunk of response.body) {
    const bytes = Buffer.from(chunk);
    if (totalBytes + bytes.length > maximumBytes) {
      await response.body.cancel();
      throw new Error("Pinned GitHub image response exceeds its per-image byte limit.");
    }
    totalBytes += bytes.length;
    onBytesRead?.(bytes.length);
    chunks.push(bytes);
  }
  return Buffer.concat(chunks, totalBytes);
}

function isAllowedGitHubUrl(url) {
  return url.protocol === "https:" && url.port === "" &&
    !url.username && !url.password &&
    (GITHUB_IMAGE_HOSTS.has(url.hostname.toLowerCase()) ||
     url.hostname.toLowerCase().endsWith(".github.com") ||
     url.hostname.toLowerCase().endsWith(".githubusercontent.com"));
}

function isGitHubAssetOrigin(value) {
  try {
    const url = new URL(value);
    if (url.protocol !== "https:" || url.port !== "" || url.username || url.password ||
        url.hostname.toLowerCase() !== "github.com") return false;
    return /^\/(?:user-attachments\/assets\/[0-9a-f-]{36}|[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/assets\/\d+\/[0-9a-f-]{36})\/?$/iu.test(url.pathname);
  } catch {
    return false;
  }
}

function isSignedGitHubProductionAssetRedirect(url, sourceUrl) {
  if (!(url instanceof URL) || !isGitHubAssetOrigin(sourceUrl) ||
      url.protocol !== "https:" || url.port !== "" || url.username || url.password ||
      url.hostname.toLowerCase() !== GITHUB_PRODUCTION_ASSET_HOST || url.hash ||
      !/^\/\d{1,20}\/\d{1,20}-[0-9a-f-]{36}\.(?:avif|bmp|gif|jpe?g|png|svg|webp|ico)$/iu.test(url.pathname) ||
      Buffer.byteLength(url.href, "utf8") > MAX_SOURCE_URL_BYTES || url.searchParams.size < 5 ||
      url.searchParams.size > 8) return false;
  const expires = Number(url.searchParams.get("X-Amz-Expires"));
  return url.searchParams.get("X-Amz-Algorithm") === "AWS4-HMAC-SHA256" &&
    /^[A-Z0-9]+\/\d{8}\/[a-z0-9-]+\/s3\/aws4_request$/iu.test(url.searchParams.get("X-Amz-Credential") || "") &&
    /^\d{8}T\d{6}Z$/u.test(url.searchParams.get("X-Amz-Date") || "") &&
    Number.isInteger(expires) && expires > 0 && expires <= 900 &&
    /^[0-9a-f]{64}$/iu.test(url.searchParams.get("X-Amz-Signature") || "") &&
    url.searchParams.get("X-Amz-SignedHeaders") === "host";
}

function normalizeGitHubUrl(value) {
  try {
    const url = new URL(value);
    if (!isAllowedGitHubUrl(url)) return "";
    url.hash = "";
    const segments = url.pathname.split("/").filter(Boolean);
    if (url.hostname.toLowerCase() === "github.com" && segments.length >= 5 &&
        (segments[2] === "blob" || segments[2] === "raw") &&
        url.searchParams.size === 1 && url.searchParams.get("raw") === "true") {
      url.search = "";
    }
    return url.href;
  } catch {
    return "";
  }
}

function urlHash(value) {
  const normalized = normalizeGitHubUrl(value);
  if (!normalized) throw new Error("Same-byte image URL alias is not a credential-free GitHub HTTPS URL.");
  return sha256(Buffer.from(normalized, "utf8"));
}

function bindUrlHash(index, asset, hash) {
  const existing = index.get(hash);
  if (existing && existing.sha256 !== asset.sha256) {
    throw new Error("One GitHub image URL hash maps to conflicting captured payloads.");
  }
  index.set(hash, asset);
}

function sniffImageMime(bytes) {
  if (bytes.length >= 8 && bytes.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))) return "image/png";
  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return "image/jpeg";
  if (bytes.length >= 6 && /^GIF8[79]a$/u.test(bytes.subarray(0, 6).toString("ascii"))) return "image/gif";
  if (bytes.length >= 12 && bytes.toString("ascii", 0, 4) === "RIFF" && bytes.toString("ascii", 8, 12) === "WEBP") return "image/webp";
  if (bytes.length >= 2 && bytes[0] === 0x42 && bytes[1] === 0x4d) return "image/bmp";
  if (bytes.length >= 4 && bytes[0] === 0x00 && bytes[1] === 0x00 && bytes[2] === 0x01 && bytes[3] === 0x00) return "image/x-icon";
  if (bytes.length >= 16 && bytes.toString("ascii", 4, 8) === "ftyp" && /(?:^|.{4})(?:avif|avis)(?:.{4}|$)/su.test(bytes.toString("ascii", 8))) return "image/avif";
  const prefix = bytes.subarray(0, Math.min(bytes.length, 4096)).toString("utf8");
  if (/^\uFEFF?\s*(?:<\?xml\b[^>]*>\s*)?(?:<!--[\s\S]*?-->\s*)*(?:<!DOCTYPE\s+svg\b[^>]*>\s*(?:<!--[\s\S]*?-->\s*)*)?<svg(?:\s|>)/iu.test(prefix)) return "image/svg+xml";
  return "";
}

function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

function escapeSegment(value) {
  return encodeURIComponent(value).replace(/[!'()*]/gu, character =>
    `%${character.charCodeAt(0).toString(16).toUpperCase()}`);
}
