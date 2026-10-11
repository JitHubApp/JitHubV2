import { createHash } from "node:crypto";
import { constants, existsSync } from "node:fs";
import { lstat, mkdir, open, readFile, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { captureVerifiedMarkdownImageAliases } from "./verified-source-image-aliases.mjs";
import {
  loadPinnedGitHubEmojiShortcodes,
  PINNED_GITHUB_EMOJI_SHORTCODES,
} from "./vendor/github-emoji-shortcodes.mjs";

const MAX_MANIFEST_BYTES = 4 * 1024 * 1024;
const MAX_README_BYTES = 16 * 1024 * 1024;
// The Contents API wraps up to 16 MiB of README data in base64 plus JSON
// metadata. Keep that response separately bounded before parsing or decoding.
const MAX_README_API_RESPONSE_BYTES = 24 * 1024 * 1024;
const PINNED_README_FETCH_TIMEOUT_MS = 60_000;
export const MAX_RENDERED_HTML_BYTES = 32 * 1024 * 1024;
const MAX_ASSET_BYTES = 64 * 1024 * 1024;
const MAX_CORPUS_ASSET_BYTES = 256 * 1024 * 1024;
const MAX_TRACKED_NETWORK_REQUESTS = 100_000;
const MAX_TRACKED_URL_BYTES = 16 * 1024 * 1024;
const MAX_ASSET_LOOKUP_ENTRIES = 15_000;
const MAX_UNAVAILABLE_IMAGE_DIAGNOSTIC_EXAMPLES = 8;
const MAX_IMAGE_REQUEST_DIAGNOSTIC_EXAMPLES = 4;
const MAX_HTML_IMAGE_SOURCE_SCAN_BYTES = 32 * 1024 * 1024;
const MAX_HTML_TAG_BYTES = 64 * 1024;
const MAX_SRCSET_CANDIDATES_PER_TAG = 32;
const MAX_NON_RENDERED_DECORATIVE_IMAGE_EXCLUSIONS = 128;
const MAX_DECORATIVE_IMAGE_URL_HASHES = 16;
export const PINNED_GFM_PARSER = Object.freeze({
  name: "marked",
  version: "18.0.5",
  license: "MIT",
  sha256: "2dc4769dfde29f51c7aca1a539c6407c789c8ea644cf8b7d01ded28a9c1d800b",
  npmIntegrity: "sha512-S6GcvALHg6K4ohtu4E7x0a1AqhAjp6cV8KhLSyN9qVapnzJkusVBxZRcIU9AeYsbe6P1hKDusSbEOzGyyuce6w==",
});
export const PINNED_GITHUB_EMOJI_PARSER = Object.freeze({
  source: PINNED_GITHUB_EMOJI_SHORTCODES.source,
  sourceCommit: PINNED_GITHUB_EMOJI_SHORTCODES.sourceCommit,
  sourceSha256: PINNED_GITHUB_EMOJI_SHORTCODES.sourceSha256,
  license: PINNED_GITHUB_EMOJI_SHORTCODES.sourceLicense,
  licenseSha256: PINNED_GITHUB_EMOJI_SHORTCODES.sourceLicenseSha256,
  mapSha256: PINNED_GITHUB_EMOJI_SHORTCODES.mapSha256,
  extensionSha256: PINNED_GITHUB_EMOJI_SHORTCODES.extensionSha256,
  aliasCount: PINNED_GITHUB_EMOJI_SHORTCODES.aliasCount,
});
export const PINNED_SOURCE_REPLAY_PARSER = Object.freeze({
  name: PINNED_GFM_PARSER.name,
  version: PINNED_GFM_PARSER.version,
  license: PINNED_GFM_PARSER.license,
  sha256: PINNED_GFM_PARSER.sha256,
  emojiShortcodeSource: PINNED_GITHUB_EMOJI_PARSER.source,
  emojiShortcodeSourceCommit: PINNED_GITHUB_EMOJI_PARSER.sourceCommit,
  emojiShortcodeSourceSha256: PINNED_GITHUB_EMOJI_PARSER.sourceSha256,
  emojiShortcodeLicense: PINNED_GITHUB_EMOJI_PARSER.license,
  emojiShortcodeLicenseSha256: PINNED_GITHUB_EMOJI_PARSER.licenseSha256,
  emojiShortcodeMapSha256: PINNED_GITHUB_EMOJI_PARSER.mapSha256,
  emojiShortcodeExtensionSha256: PINNED_GITHUB_EMOJI_PARSER.extensionSha256,
  emojiShortcodeAliasCount: PINNED_GITHUB_EMOJI_PARSER.aliasCount,
});
const MARKED_PARSER_PATH = path.join(
  path.dirname(fileURLToPath(import.meta.url)),
  "vendor",
  `marked-${PINNED_GFM_PARSER.version}.umd.js`,
);
const GITHUB_PRIMER_LIGHT_V4_CSS_PATH = path.join(
  path.dirname(fileURLToPath(import.meta.url)),
  "profiles",
  "github-primer-light-v4.css",
);
const require = createRequire(import.meta.url);
const SHA256_PATTERN = /^[0-9a-f]{64}$/u;
const GIT_SHA1_PATTERN = /^[0-9a-f]{40}$/u;
export const PINNED_GITHUB_README_STYLE_PROFILE = Object.freeze({
  id: "github-primer-light",
  version: 4,
  colorScheme: "light",
  cssPath: "/github-primer-light-v4.css",
  sha256: "d1f276efd42d802835c33480c90cd54544afab7a20eae1be00d8b0a7f881f8f1",
});
const RENDERED_SNAPSHOT_FAILURE_PREDICATES = new Set([
  "active-element-tag",
  "duplicate-image-index",
  "event-handler-attribute",
  "external-css-resource",
  "external-resource-attribute",
  "invalid-root",
  "missing-data-image",
  "missing-image-index",
  "network-image-data-payload",
  "stylesheet-link",
  "style-element",
]);
const SNAPSHOT_ACTIVE_TAGS = new Set(["script", "iframe", "object", "embed", "base"]);
const SNAPSHOT_RESOURCE_ATTRIBUTES = new Set(["src", "srcset", "sizes", "poster", "background"]);
const SNAPSHOT_DIAGNOSTIC_TAGS = new Set([
  "a", "article", "audio", "base", "canvas", "code", "div", "embed", "form", "iframe", "img", "link",
  "object", "p", "picture", "pre", "script", "source", "span", "style", "svg", "table", "use", "video",
]);
const SNAPSHOT_DIAGNOSTIC_ATTRIBUTES = new Set([
  ...SNAPSHOT_RESOURCE_ATTRIBUTES,
  "data-jithub-image-index",
  "rel",
  "style",
]);
const CAPTURED_IMAGE_MIMES = new Set([
  "image/avif",
  "image/bmp",
  "image/gif",
  "image/jpeg",
  "image/png",
  "image/svg+xml",
  "image/webp",
  "image/x-icon",
]);
const SAFE_CDP_BLOCKED_REASONS = new Set([
  "other",
  "csp",
  "mixed-content",
  "origin",
  "inspector",
  "subresource-filter",
  "content-type",
  "coep-frame-resource-needs-coep-header",
  "corp-not-same-origin-after-defaulted-to-same-origin-by-coep",
  "corp-not-same-site",
]);
const SAFE_CDP_RESOURCE_TYPES = new Set([
  "Document", "Stylesheet", "Image", "Media", "Font", "Script", "XHR", "Fetch",
  "EventSource", "WebSocket", "Manifest", "Other", "Preflight", "Ping", "CSPViolationReport",
  "SignedExchange", "WebBundle", "WebTransport",
]);

export async function captureSameByteCorpus({
  directory,
  repository,
  readmeUrl,
  readmePath,
  readmeGitBlobSha1,
  readmeByteSize,
  images,
  nonRenderedDecorativeImages = [],
  renderedHtml,
  readmeRendered = true,
  responseRecorder,
  fetchImpl = fetch,
  githubToken = process.env.JITHUB_README_AUDIT_GITHUB_TOKEN,
}) {
  if (!directory || !repository?.fullName || !repository?.commitSha ||
      !readmeUrl || !readmePath || !isGitSha1(readmeGitBlobSha1) ||
      !Number.isSafeInteger(readmeByteSize) || readmeByteSize < 0) {
    throw new Error("Same-byte capture requires a pinned public README identity and byte size.");
  }
  const parsedReadmeUrl = new URL(readmeUrl);
  if (parsedReadmeUrl.protocol !== "https:" ||
      !["raw.githubusercontent.com", "github.com"].includes(parsedReadmeUrl.hostname.toLowerCase()) ||
      parsedReadmeUrl.username || parsedReadmeUrl.password) {
    throw new Error("Same-byte README capture accepts only credential-free GitHub HTTPS URLs.");
  }
  if (readmeByteSize > MAX_README_BYTES) {
    throw new Error(`README exceeds the ${MAX_README_BYTES}-byte corpus safety limit.`);
  }

  const response = await fetchImpl(parsedReadmeUrl, {
    headers: { "user-agent": "JitHub-Readme-Same-Byte-Capture" },
    redirect: "follow",
    signal: AbortSignal.timeout(PINNED_README_FETCH_TIMEOUT_MS),
  });
  if (response.url) {
    const finalUrl = new URL(response.url);
    if (finalUrl.protocol !== "https:" ||
        !["raw.githubusercontent.com", "github.com"].includes(finalUrl.hostname.toLowerCase()) ||
        finalUrl.username || finalUrl.password) {
      throw new Error("Pinned README redirected outside the credential-free GitHub HTTPS source set.");
    }
  }
  const throttled = response.status === 403 || response.status === 429 || response.status === 503;
  if ((!response.ok || !response.body) && !throttled) {
    throw new Error(`Pinned README fetch failed with HTTP ${response.status}.`);
  }
  if (throttled && (!githubToken || typeof githubToken !== "string")) {
    throw new Error(`Pinned README fetch failed with HTTP ${response.status}; authenticated pinned fallback is unavailable.`);
  }
  let readmeBytes = response.ok && response.body
    ? await readBoundedResponse(response, MAX_README_BYTES)
    : null;
  if (!readmeBytes || readmeBytes.length !== readmeByteSize ||
      gitBlobSha1(readmeBytes) !== readmeGitBlobSha1.toLowerCase()) {
    // A raw GitHub URL addresses the symlink blob itself. GitHub's README
    // Contents endpoint resolves that symlink at the pinned commit, which is
    // the same source representation GitHub renders. Accept it only when both
    // API metadata and the locally recomputed Git blob identity match the
    // immutable audit manifest.
    readmeBytes = await fetchResolvedPinnedReadme({
      repository,
      readmeGitBlobSha1: readmeGitBlobSha1.toLowerCase(),
      readmeByteSize,
      githubToken,
      fetchImpl,
    });
  }

  if (typeof readmeRendered !== "boolean" ||
      (readmeRendered && typeof renderedHtml !== "string") ||
      (!readmeRendered && (renderedHtml !== undefined || images?.length))) {
    throw new Error("Same-byte capture requires a bounded rendered HTML snapshot or an explicit source-view status.");
  }
  const decorativeImageExclusions = createNonRenderedDecorativeImageExclusionEntries(
    nonRenderedDecorativeImages,
    readmeUrl,
  );
  if (!readmeRendered && decorativeImageExclusions.length > 0) {
    throw new Error("A GitHub source-view corpus cannot contain rendered decorative-image exclusions.");
  }
  const renderedHtmlBytes = readmeRendered ? Buffer.from(renderedHtml, "utf8") : null;
  if (renderedHtmlBytes && (renderedHtmlBytes.length === 0 || renderedHtmlBytes.length > MAX_RENDERED_HTML_BYTES)) {
    throw new Error(`Rendered GitHub HTML exceeds the ${MAX_RENDERED_HTML_BYTES}-byte safety limit or is empty.`);
  }

  const captured = await responseRecorder.captureVisibleImages(images);
  const markdownImageSources = readmeRendered
    ? await collectPinnedMarkdownImageSources(readmeBytes)
    : [];
  const sourceAssets = await captureVerifiedMarkdownImageAliases({
    sources: markdownImageSources,
    repository,
    commitSha: repository.commitSha,
    readmePath,
    assets: captured.assets,
    fetchImpl,
    maxAssetBytes: MAX_ASSET_BYTES,
    maxAggregateAssetBytes: MAX_CORPUS_ASSET_BYTES,
    maxLookupEntries: MAX_ASSET_LOOKUP_ENTRIES,
  });
  const assets = sourceAssets.assets;
  const imageRoutes = captured.imageRoutes;
  let totalAssetBytes = 0;
  for (const asset of assets) {
    if (!SHA256_PATTERN.test(asset.sha256) || asset.bytes.length > MAX_ASSET_BYTES) {
      throw new Error("Captured image is malformed or exceeds its per-resource byte limit.");
    }
    totalAssetBytes += asset.bytes.length;
  }
  if (totalAssetBytes > MAX_CORPUS_ASSET_BYTES) {
    throw new Error(`Captured assets exceed the ${MAX_CORPUS_ASSET_BYTES}-byte corpus safety limit.`);
  }
  if (renderedHtmlBytes) validateRenderedSnapshot(renderedHtml, imageRoutes);

  const output = path.resolve(directory);
  await mkdir(path.join(output, "assets"), { recursive: true });
  await writeFile(path.join(output, "readme.md"), readmeBytes);
  if (renderedHtmlBytes) await writeFile(path.join(output, "rendered.html"), renderedHtmlBytes);
  for (const asset of assets) {
    const assetPath = path.join(output, "assets", asset.sha256);
    if (!existsSync(assetPath)) await writeFile(assetPath, asset.bytes, { flag: "wx" });
  }

  const assetEntries = assets
    .flatMap(asset => asset.urlHashes.map(urlSha256Value => ({
      urlSha256: urlSha256Value,
      sha256: asset.sha256,
      byteSize: asset.bytes.length,
      mimeType: asset.mimeType,
    })))
    .sort((left, right) => left.urlSha256 < right.urlSha256 ? -1 : left.urlSha256 > right.urlSha256 ? 1 : 0);
  if (assetEntries.length > MAX_ASSET_LOOKUP_ENTRIES) {
    throw new Error("Same-byte corpus exceeds its bounded URL lookup entry count.");
  }
  const manifest = {
    schemaVersion: 2,
    complete: true,
    repository: {
      fullName: repository.fullName,
      commitSha: repository.commitSha.toLowerCase(),
      readmePath,
      readmeGitBlobSha1: readmeGitBlobSha1.toLowerCase(),
    },
    readme: {
      file: "readme.md",
      byteSize: readmeBytes.length,
      sha256: sha256(readmeBytes),
    },
    assets: assetEntries,
    imageRoutes,
    nonRenderedDecorativeImageExclusions: decorativeImageExclusions,
    browserRender: renderedHtmlBytes
      ? {
        status: "passed",
        file: "rendered.html",
        byteSize: renderedHtmlBytes.length,
        sha256: sha256(renderedHtmlBytes),
      }
      : { status: "not-applicable", reason: "github-source-view" },
    limits: {
      maxReadmeBytes: MAX_README_BYTES,
      maxRenderedHtmlBytes: MAX_RENDERED_HTML_BYTES,
      maxAssetBytes: MAX_ASSET_BYTES,
      maxAggregateAssetBytes: MAX_CORPUS_ASSET_BYTES,
    },
  };
  const manifestBytes = Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`, "utf8");
  if (manifestBytes.length > MAX_MANIFEST_BYTES) {
    throw new Error(`Same-byte corpus manifest exceeds the ${MAX_MANIFEST_BYTES}-byte safety limit.`);
  }
  await writeFile(path.join(output, "manifest.json"), manifestBytes);
  return {
    directory: output,
    assetCount: assets.length,
    assetBytes: totalAssetBytes,
    sourceImageAliases: {
      reusedCount: sourceAssets.reusedCount,
      fetchedCount: sourceAssets.fetchedCount,
      skippedCount: sourceAssets.skippedCount,
      byteMismatchCount: sourceAssets.byteMismatchCount,
      sourceFetchAttemptCount: sourceAssets.sourceFetchAttemptCount,
      sourceFetchBytes: sourceAssets.sourceFetchBytes,
      sourceFetchBudgetExhausted: sourceAssets.sourceFetchBudgetExhausted,
    },
    readmeBytes: readmeBytes.length,
    readmeSha256: sha256(readmeBytes),
    browserRender: manifest.browserRender,
    renderedHtmlBytes,
    assetUrlMapSha256: sha256(Buffer.from(JSON.stringify(assetEntries), "utf8")),
    manifestSha256: sha256(manifestBytes),
  };
}

async function collectPinnedMarkdownImageSources(readmeBytes) {
  const parserBytes = await readBoundedFile(MARKED_PARSER_PATH, 8 * 1024 * 1024);
  if (sha256(parserBytes) !== PINNED_GFM_PARSER.sha256) {
    throw new Error("Pinned Markdown image extraction parser does not match its recorded SHA-256.");
  }
  const parserModule = require(MARKED_PARSER_PATH);
  const lexer = parserModule?.marked?.lexer;
  if (typeof lexer !== "function") {
    throw new Error("Pinned Markdown image extraction parser is unavailable.");
  }
  const tokens = lexer(readmeBytes.toString("utf8"), {
    gfm: true,
    breaks: false,
    pedantic: false,
  });
  const pending = [...tokens].reverse();
  const markdownSources = [];
  const responsiveSources = [];
  const htmlImageSources = [];
  const seen = new Set();
  let scannedHtmlBytes = 0;
  while (pending.length > 0) {
    const token = pending.pop();
    if (!token || typeof token !== "object") continue;
    if (token.type === "image" && typeof token.href === "string") {
      markdownSources.push(token.href);
    }
    if ((token.type === "html" || token.type === "table") && typeof token.raw === "string") {
      scannedHtmlBytes += Buffer.byteLength(token.raw, "utf8");
      if (scannedHtmlBytes > MAX_HTML_IMAGE_SOURCE_SCAN_BYTES) {
        throw new Error("Pinned README exceeds the bounded HTML image-source scan budget.");
      }
      const extracted = extractHtmlImageSourceCandidates(token.raw);
      responsiveSources.push(...extracted.responsive);
      htmlImageSources.push(...extracted.images);
    }
    if (Array.isArray(token.tokens)) pending.push(...[...token.tokens].reverse());
    if (Array.isArray(token.items)) {
      for (const item of [...token.items].reverse()) {
        if (Array.isArray(item?.tokens)) pending.push(...[...item.tokens].reverse());
      }
    }
  }
  // Prefer browser-selected <picture>/<source srcset> candidates to their
  // <img src> fallback. The helper accepts an alias only when those exact
  // candidate bytes already exist in the browser capture.
  const sources = [];
  for (const source of [...responsiveSources, ...markdownSources, ...htmlImageSources]) {
    if (typeof source !== "string" || source.length === 0 || seen.has(source)) continue;
    seen.add(source);
    sources.push(source);
    if (sources.length > MAX_ASSET_LOOKUP_ENTRIES) {
      throw new Error("Pinned README exceeds the bounded Markdown/HTML image source count.");
    }
  }
  return sources;
}

function extractHtmlImageSourceCandidates(raw) {
  const responsive = [];
  const images = [];
  let index = 0;
  while (index < raw.length) {
    const open = raw.indexOf("<", index);
    if (open < 0) break;
    if (raw.startsWith("<!--", open)) {
      const commentEnd = raw.indexOf("-->", open + 4);
      index = commentEnd < 0 ? raw.length : commentEnd + 3;
      continue;
    }

    const end = findHtmlTagEnd(raw, open);
    if (end < 0) {
      index = open + 1;
      continue;
    }
    const tag = raw.slice(open, end);
    const tagName = /^<\s*([A-Za-z][A-Za-z0-9:-]*)\b/u.exec(tag)?.[1]?.toLowerCase();
    if (!tagName) {
      index = end;
      continue;
    }
    if (["script", "style", "textarea", "title"].includes(tagName) && !/^<\s*\//u.test(tag)) {
      const closeStart = raw.toLowerCase().indexOf(`</${tagName}`, end);
      if (closeStart < 0) break;
      const closeEnd = findHtmlTagEnd(raw, closeStart);
      index = closeEnd < 0 ? raw.length : closeEnd;
      continue;
    }

    if (!/^<\s*\//u.test(tag)) {
      const attributes = readHtmlAttributes(tag, tagName);
      if (attributes) {
        if (tagName === "source" && attributes.has("srcset")) {
          responsive.push(...parseSrcsetUrls(attributes.get("srcset")));
        } else if (tagName === "img") {
          for (const name of ["data-canonical-src", "src"]) {
            const value = attributes.get(name);
            if (value) images.push(value);
          }
        }
      }
    }
    index = end;
  }
  return { responsive, images };
}

function findHtmlTagEnd(raw, open) {
  let quote = "";
  const limit = Math.min(raw.length, open + MAX_HTML_TAG_BYTES);
  for (let index = open + 1; index < limit; index++) {
    const character = raw[index];
    if (quote) {
      if (character === quote) quote = "";
    } else if (character === "\"" || character === "'") {
      quote = character;
    } else if (character === ">") {
      return index + 1;
    }
  }
  return -1;
}

function readHtmlAttributes(tag, tagName) {
  const names = new Set(["src", "data-canonical-src", "srcset"]);
  const found = new Map();
  let index = 1;
  while (index < tag.length && /[\t\n\f\r ]/u.test(tag[index])) index++;
  while (index < tag.length && /[A-Za-z0-9:-]/u.test(tag[index])) index++;
  if (tag.slice(1, index).toLowerCase() !== tagName) return null;
  while (index < tag.length) {
    while (index < tag.length && /[\t\n\f\r /]/u.test(tag[index])) index++;
    if (index >= tag.length || tag[index] === ">") break;
    const nameStart = index;
    while (index < tag.length && !/[\t\n\f\r />=]/u.test(tag[index])) index++;
    if (nameStart === index) return null;
    const name = tag.slice(nameStart, index).toLowerCase();
    while (index < tag.length && /[\t\n\f\r ]/u.test(tag[index])) index++;
    let value = "";
    if (tag[index] === "=") {
      index++;
      while (index < tag.length && /[\t\n\f\r ]/u.test(tag[index])) index++;
      if (tag[index] === "\"" || tag[index] === "'") {
        const quote = tag[index++];
        const valueStart = index;
        while (index < tag.length && tag[index] !== quote) index++;
        if (index >= tag.length) return null;
        value = tag.slice(valueStart, index++);
      } else {
        const valueStart = index;
        while (index < tag.length && !/[\t\n\f\r >]/u.test(tag[index])) {
          if (tag[index] === "\"" || tag[index] === "'" || tag[index] === "`" || tag[index] === "<") return null;
          index++;
        }
        value = tag.slice(valueStart, index);
      }
    }
    if (names.has(name)) {
      if (found.has(name)) return null;
      const decoded = decodeHtmlImageAttribute(value);
      if (decoded === null) return null;
      found.set(name, decoded);
    }
  }
  return found;
}

function decodeHtmlImageAttribute(value) {
  if (!value.includes("&")) return value;
  const named = new Map([["amp", "&"], ["quot", "\""], ["apos", "'"], ["lt", "<"], ["gt", ">"]]);
  const entityPattern = /&(?:#(x[0-9a-f]{1,6}|[0-9]{1,7})|([A-Za-z][A-Za-z0-9]{0,31}));/giu;
  let output = "";
  let cursor = 0;
  for (const match of value.matchAll(entityPattern)) {
    const prefix = value.slice(cursor, match.index);
    if (prefix.includes("&")) return null;
    output += prefix;
    let replacement;
    if (match[1]) {
      const numeric = match[1].toLowerCase();
      const codePoint = numeric.startsWith("x")
        ? Number.parseInt(numeric.slice(1), 16)
        : Number.parseInt(numeric, 10);
      if (!Number.isInteger(codePoint) || codePoint <= 0 || codePoint > 0x10ffff ||
          (codePoint >= 0xd800 && codePoint <= 0xdfff)) return null;
      replacement = String.fromCodePoint(codePoint);
    } else {
      replacement = named.get(match[2].toLowerCase());
      if (replacement === undefined) return null;
    }
    output += replacement;
    cursor = match.index + match[0].length;
  }
  if (value.slice(cursor).includes("&")) return null;
  output += value.slice(cursor);
  return output;
}

function parseSrcsetUrls(value) {
  const candidates = [];
  let index = 0;
  while (index < value.length) {
    while (index < value.length && /[\t\n\f\r ,]/u.test(value[index])) index++;
    if (index >= value.length) break;
    const start = index;
    while (index < value.length && !/[\t\n\f\r ]/u.test(value[index])) index++;
    let url = value.slice(start, index);
    let hadTrailingComma = false;
    while (url.endsWith(",")) {
      hadTrailingComma = true;
      url = url.slice(0, -1);
    }
    if (url && !hadTrailingComma) {
      let depth = 0;
      while (index < value.length) {
        const character = value[index++];
        if (character === "(") depth++;
        else if (character === ")" && depth > 0) depth--;
        else if (character === "," && depth === 0) break;
      }
    }
    if (url) candidates.push(url);
    if (candidates.length > MAX_SRCSET_CANDIDATES_PER_TAG) return [];
  }
  return candidates;
}

export function createNonRenderedDecorativeImageExclusionEntries(images, readmeUrl) {
  if (!Array.isArray(images) || images.length > MAX_NON_RENDERED_DECORATIVE_IMAGE_EXCLUSIONS) {
    throw new Error("Same-byte corpus exceeds its bounded non-rendered decorative-image evidence count.");
  }
  const seenReplayIndexes = new Set();
  return images.map(image => {
    if (!image || !Number.isSafeInteger(image.replayIndex) || image.replayIndex < 0 ||
        image.replayIndex >= MAX_ASSET_LOOKUP_ENTRIES || seenReplayIndexes.has(image.replayIndex) ||
        image.explicitEmptyAlt !== true || image.failedDecode !== true || image.zeroBounds !== true) {
      throw new Error("Same-byte corpus contains unproven or ambiguous non-rendered decorative-image evidence.");
    }
    seenReplayIndexes.add(image.replayIndex);
    const urlSha256s = new Set();
    for (const value of [image.canonicalSource, image.source, image.currentSource]) {
      if (typeof value !== "string" || value.length === 0 || /^data:/iu.test(value)) continue;
      let normalized;
      try {
        normalized = normalizeHttpUrl(new URL(value, readmeUrl).href);
      } catch {
        normalized = "";
      }
      if (!normalized) continue;
      urlSha256s.add(sha256(Buffer.from(normalized, "utf8")));
    }
    if (urlSha256s.size === 0 || urlSha256s.size > MAX_DECORATIVE_IMAGE_URL_HASHES) {
      throw new Error("Same-byte corpus contains a non-rendered image without a bounded HTTP source identity.");
    }
    return {
      urlSha256s: [...urlSha256s].sort(),
      explicitEmptyAlt: true,
      failedDecode: true,
      zeroBounds: true,
    };
  });
}

function boundedHttpStatus(value) {
  return Number.isInteger(value) && value >= 100 && value <= 599 ? value : null;
}

function safeNetworkFailureCode(value) {
  return typeof value === "string" && /^net::ERR_[A-Z0-9_]{1,64}$/u.test(value)
    ? value
    : null;
}

function safeBlockedReason(value) {
  return typeof value === "string" && SAFE_CDP_BLOCKED_REASONS.has(value) ? value : null;
}

function safeResourceType(value) {
  return typeof value === "string" && SAFE_CDP_RESOURCE_TYPES.has(value) ? value : "Other";
}

function classifyUnavailableImageRequest(record) {
  if (record.blockedReason) return "browser-blocked";
  if (record.failed) return record.canceled ? "network-canceled" : "network-failed";
  if (record.status >= 400) return "http-error";
  if (record.status >= 300) return "http-non-success";
  if (record.status >= 200 && record.status < 300) {
    return record.finished ? "http-success-image-unavailable" : "http-response-incomplete";
  }
  return record.finished ? "finished-without-http-status" : "no-final-response";
}

export function createResponseRecorder(cdp, { maxAssetBytes = MAX_ASSET_BYTES } = {}) {
  const requests = new Map();
  const cleanups = [];
  let recorderFailure = "";
  let retainedUrlBytes = 0;
  const addListener = (name, listener) => cleanups.push(cdp.on(name, listener));
  const addRequestUrl = (record, value) => {
    if (typeof value !== "string" || value.length === 0) return;
    if (record.urls.has(value)) return;
    if (value.length > 8192 || (record.urls.size >= 16 && !record.urls.has(value))) {
      recorderFailure = "Edge network request trace exceeded a bounded URL alias limit.";
      return;
    }
    const retainedBytes = value.length * 2;
    if (retainedUrlBytes + retainedBytes > MAX_TRACKED_URL_BYTES) {
      recorderFailure = "Edge network request trace exceeded its bounded URL metadata budget.";
      return;
    }
    retainedUrlBytes += retainedBytes;
    record.urls.add(value);
  };

  addListener("Network.requestWillBeSent", event => {
    let record = requests.get(event.requestId);
    if (!record) {
      if (requests.size >= MAX_TRACKED_NETWORK_REQUESTS) {
        recorderFailure = "Edge network request trace exceeded its bounded request count.";
        return;
      }
      record = { requestId: event.requestId, urls: new Set(), status: 0, mimeType: "", type: "", finished: false, failed: false };
      requests.set(event.requestId, record);
    }
    if (event.type) record.type = safeResourceType(event.type);
    if (event.redirectResponse) {
      record.status = event.redirectResponse.status || record.status;
      record.mimeType = event.redirectResponse.mimeType || record.mimeType;
    }
    if (event.request?.url) addRequestUrl(record, event.request.url);
  });
  addListener("Network.responseReceived", event => {
    const record = requests.get(event.requestId);
    if (!record) return;
    addRequestUrl(record, event.response.url);
    record.status = boundedHttpStatus(event.response.status) || 0;
    record.mimeType = event.response.mimeType || "";
    if (event.type) record.type = safeResourceType(event.type);
  });
  addListener("Network.loadingFinished", event => {
    const record = requests.get(event.requestId);
    if (!record) return;
    record.finished = true;
    record.encodedDataLength = event.encodedDataLength;
  });
  addListener("Network.loadingFailed", event => {
    const record = requests.get(event.requestId);
    if (!record) return;
    record.failed = true;
    record.failureCode = safeNetworkFailureCode(event.errorText);
    record.blockedReason = safeBlockedReason(event.blockedReason);
    record.canceled = event.canceled === true;
  });

  return {
    async captureVisibleImages(images) {
      if (recorderFailure) throw new Error(recorderFailure);
      if (!Array.isArray(images) || images.length > MAX_ASSET_LOOKUP_ENTRIES) {
        throw new Error("Same-byte corpus exceeds its bounded image-element count.");
      }
      const selected = new Map();
      const dataImages = [];
      for (let ordinal = 0; ordinal < images.length; ordinal++) {
        const image = images[ordinal];
        if (!image.complete || image.naturalWidth <= 0 || image.naturalHeight <= 0) {
          throw new Error("A browser-visible image was not fully decoded during same-byte capture.");
        }
        const replayIndex = image.replayIndex ?? ordinal;
        if (!Number.isSafeInteger(replayIndex) || replayIndex < 0 || replayIndex >= MAX_ASSET_LOOKUP_ENTRIES) {
          throw new Error("Same-byte corpus contains an invalid rendered image index.");
        }
        const aliases = [...new Set([image.currentSource, image.source, image.canonicalSource]
          .filter(value => typeof value === "string" && value.length > 0))];
        const selectedUrl = image.currentSource || image.source;
        if (!selectedUrl) throw new Error("A visible browser image has no selected source URL.");
        if (/^data:image\//iu.test(selectedUrl)) {
          if (Buffer.byteLength(selectedUrl, "utf8") > MAX_RENDERED_HTML_BYTES) {
            throw new Error("A rendered data image exceeds the bounded HTML snapshot size.");
          }
          dataImages.push({ index: replayIndex, dataUri: true });
          continue;
        }
        const selectedKey = normalizeHttpUrl(selectedUrl);
        if (!selectedKey) throw new Error("A visible browser image has an unsupported resource URL.");
        const aliasKeys = aliases.map(normalizeHttpUrl).filter(Boolean);
        const selectedEntry = selected.get(selectedKey) ?? { aliases: new Set(), indexes: [] };
        for (const aliasKey of aliasKeys) selectedEntry.aliases.add(aliasKey);
        selectedEntry.indexes.push(replayIndex);
        selected.set(selectedKey, selectedEntry);
      }

      // Index once: a long README can have thousands of visible images and
      // many unrelated network requests. Re-scanning every request per image
      // would make capture quadratic in precisely the stress cases we audit.
      const requestsByUrl = new Map();
      for (const record of requests.values()) {
        if (record.type !== "Image") continue;
        record.normalizedUrls = new Set([...record.urls].map(normalizeHttpUrl).filter(Boolean));
        for (const key of record.normalizedUrls) {
          const matches = requestsByUrl.get(key) ?? [];
          matches.push(record);
          requestsByUrl.set(key, matches);
        }
      }

      const captureWork = [];
      const lookupKeys = new Set();
      for (const [requestedUrl, imageSelection] of selected) {
        const matches = requestsByUrl.get(requestedUrl) ?? [];
        if (matches.length === 0) {
          throw new Error(`No captured network response matches visible image key ${resourceUrlSha256(requestedUrl)}.`);
        }
        const successful = matches.filter(record => record.status >= 200 && record.status < 300 && record.finished);
        if (successful.length === 0) {
          throw new Error(`Visible image key ${resourceUrlSha256(requestedUrl)} has no completed successful response.`);
        }
        const aliases = new Set(imageSelection.aliases);
        for (const record of matches) {
          for (const key of record.normalizedUrls) aliases.add(key);
        }
        for (const key of aliases) lookupKeys.add(key);
        if (lookupKeys.size > MAX_ASSET_LOOKUP_ENTRIES) {
          throw new Error("Same-byte corpus exceeds its bounded URL lookup entry count.");
        }
        captureWork.push({ requestedUrl, successful, aliases, indexes: imageSelection.indexes });
      }

      const assetsByHash = new Map();
      const imageRoutes = [...dataImages];
      let uniquePayloadBytes = 0;
      for (const { requestedUrl, successful, aliases, indexes } of captureWork) {
        let chosenBytes = null;
        let chosenHash = "";
        for (const record of successful) {
          if (Number.isFinite(record.encodedDataLength) && record.encodedDataLength > maxAssetBytes) {
            throw new Error(`Visible image key ${resourceUrlSha256(requestedUrl)} exceeds the per-image byte limit.`);
          }
          const result = await cdp.send("Network.getResponseBody", { requestId: record.requestId });
          const bodySizeUpperBound = result.base64Encoded
            ? Math.floor(result.body.length * 3 / 4)
            : Buffer.byteLength(result.body || "", "utf8");
          if (bodySizeUpperBound > maxAssetBytes) {
            throw new Error(`Visible image key ${resourceUrlSha256(requestedUrl)} exceeds the per-image byte limit.`);
          }
          const bytes = Buffer.from(result.body || "", result.base64Encoded ? "base64" : "utf8");
          if (bytes.length > maxAssetBytes || bytes.length === 0) {
            throw new Error(`Visible image key ${resourceUrlSha256(requestedUrl)} is empty or exceeds the per-image byte limit.`);
          }
          const responseHash = sha256(bytes);
          if (chosenBytes === null) {
            chosenBytes = bytes;
            chosenHash = responseHash;
          } else if (chosenHash !== responseHash) {
            throw new Error(`Visible image key ${resourceUrlSha256(requestedUrl)} returned different bytes during one page capture.`);
          }
        }
        if (chosenBytes === null) {
          throw new Error(`Visible image key ${resourceUrlSha256(requestedUrl)} returned an empty response body.`);
        }
        const chosenMimeType = detectCapturedImageMime(chosenBytes);
        if (!chosenMimeType) {
          throw new Error(`Visible image key ${resourceUrlSha256(requestedUrl)} has an unsupported or unrecognized image payload.`);
        }
        let asset = assetsByHash.get(chosenHash);
        if (!asset) {
          uniquePayloadBytes += chosenBytes.length;
          if (uniquePayloadBytes > MAX_CORPUS_ASSET_BYTES) {
            throw new Error(`Captured assets exceed the ${MAX_CORPUS_ASSET_BYTES}-byte corpus safety limit.`);
          }
          asset = { sha256: chosenHash, bytes: chosenBytes, mimeType: chosenMimeType, urlHashes: new Set() };
          assetsByHash.set(chosenHash, asset);
        }
        for (const alias of aliases) {
          asset.urlHashes.add(resourceUrlSha256(alias));
        }
        const selectedUrlSha256 = resourceUrlSha256(requestedUrl);
        for (const index of indexes) {
          imageRoutes.push({ index, urlSha256: selectedUrlSha256, sha256: chosenHash, mimeType: chosenMimeType });
        }
      }
      imageRoutes.sort((left, right) => left.index - right.index);
      if (imageRoutes.length !== images.length || new Set(imageRoutes.map(route => route.index)).size !== imageRoutes.length) {
        throw new Error("Same-byte image routes do not cover each visible image exactly once.");
      }
      return {
        assets: [...assetsByHash.values()]
        .map(asset => ({ ...asset, urlHashes: [...asset.urlHashes].sort() }))
        .sort((left, right) => left.sha256 < right.sha256 ? -1 : left.sha256 > right.sha256 ? 1 : 0),
        imageRoutes,
      };
    },
    getUnavailableVisibleImageResponseEvidence(images) {
      if (!Array.isArray(images) || images.length > MAX_ASSET_LOOKUP_ENTRIES) {
        return {
          traceStatus: "invalid-image-list",
          unavailableImageCount: 0,
          examples: [],
        };
      }
      const unavailable = images.filter(image => !image || image.complete !== true ||
        !Number.isFinite(image.naturalWidth) || image.naturalWidth <= 0 ||
        !Number.isFinite(image.naturalHeight) || image.naturalHeight <= 0);
      const examples = unavailable.slice(0, MAX_UNAVAILABLE_IMAGE_DIAGNOSTIC_EXAMPLES);
      const targetUrls = new Set();
      for (const image of examples) {
        for (const value of [image?.currentSource, image?.source, image?.canonicalSource]) {
          const normalized = normalizeHttpUrl(value);
          if (normalized) targetUrls.add(normalized);
        }
      }

      // This diagnostic runs only on the existing hard-failure path. Keep its
      // work bounded by the recorder's request and alias ceilings, and index
      // only URLs belonging to the at-most-eight unavailable-image examples.
      const requestsByUrl = new Map();
      for (const record of requests.values()) {
        const visitedRecordUrls = new Set();
        for (const value of record.urls) {
          const normalized = normalizeHttpUrl(value);
          if (!normalized || !targetUrls.has(normalized) || visitedRecordUrls.has(normalized)) continue;
          visitedRecordUrls.add(normalized);
          const matches = requestsByUrl.get(normalized) ?? { count: 0, examples: [] };
          matches.count++;
          if (matches.examples.length < MAX_IMAGE_REQUEST_DIAGNOSTIC_EXAMPLES) {
            matches.examples.push(record);
          }
          requestsByUrl.set(normalized, matches);
        }
      }

      return {
        traceStatus: recorderFailure ? "bounded-network-trace-incomplete" : "observed",
        unavailableImageCount: unavailable.length,
        examples: examples.map(image => {
          const sources = [];
          const seen = new Set();
          for (const value of [image?.currentSource, image?.source, image?.canonicalSource]) {
            const normalized = normalizeHttpUrl(value);
            if (!normalized || seen.has(normalized)) continue;
            seen.add(normalized);
            const matches = requestsByUrl.get(normalized) ?? { count: 0, examples: [] };
            const matchingRequestCount = matches.count;
            sources.push({
              urlSha256: resourceUrlSha256(normalized),
              matchingRequestCount,
              requestsTruncated: matchingRequestCount > MAX_IMAGE_REQUEST_DIAGNOSTIC_EXAMPLES,
              requests: matches.examples.map(record => ({
                resourceType: safeResourceType(record.type),
                httpStatus: boundedHttpStatus(record.status),
                finished: record.finished === true,
                failed: record.failed === true,
                classification: classifyUnavailableImageRequest(record),
                networkFailureCode: record.failureCode || null,
                blockedReason: record.blockedReason || null,
                canceled: record.canceled === true,
              })),
            });
          }
          return {
            replayIndex: Number.isSafeInteger(image?.replayIndex) && image.replayIndex >= 0 &&
              image.replayIndex < MAX_ASSET_LOOKUP_ENTRIES ? image.replayIndex : null,
            sources,
          };
        }),
      };
    },
    dispose() {
      for (const cleanup of cleanups.splice(0)) cleanup?.();
      requests.clear();
    },
  };
}

export async function loadSameByteCorpus(directory) {
  const root = path.resolve(directory);
  const manifestPath = path.join(root, "manifest.json");
  const manifestBytes = await readBoundedFile(manifestPath, MAX_MANIFEST_BYTES);
  const manifest = JSON.parse(manifestBytes.toString("utf8"));
  validateManifestShape(manifest);
  const readmePath = path.join(root, manifest.readme.file);
  const readmeBytes = await readBoundedFile(readmePath, MAX_README_BYTES);
  if (readmeBytes.length !== manifest.readme.byteSize || sha256(readmeBytes) !== manifest.readme.sha256) {
    throw new Error("Same-byte corpus README is missing or does not match its recorded SHA-256.");
  }
  if (gitBlobSha1(readmeBytes) !== manifest.repository.readmeGitBlobSha1) {
    throw new Error("Same-byte corpus README does not match the pinned GitHub blob SHA-1.");
  }
  const renderedHtmlBytes = manifest.browserRender.status === "passed"
    ? await readBoundedFile(path.join(root, manifest.browserRender.file), MAX_RENDERED_HTML_BYTES)
    : null;
  if (renderedHtmlBytes &&
      (renderedHtmlBytes.length !== manifest.browserRender.byteSize ||
       sha256(renderedHtmlBytes) !== manifest.browserRender.sha256)) {
    throw new Error("Same-byte corpus rendered HTML is missing or does not match its recorded SHA-256.");
  }
  if (renderedHtmlBytes) validateRenderedSnapshot(renderedHtmlBytes.toString("utf8"), manifest.imageRoutes);

  const assets = new Map();
  const entriesByUrlHash = new Map();
  let totalBytes = 0;
  for (const item of manifest.assets) {
    if (!SHA256_PATTERN.test(item.sha256) || !Number.isSafeInteger(item.byteSize) ||
        item.byteSize <= 0 || item.byteSize > MAX_ASSET_BYTES || !isImageMime(item.mimeType)) {
      throw new Error("Same-byte corpus contains an invalid image entry.");
    }
    let asset = assets.get(item.sha256);
    if (!asset) {
      asset = await loadAsset(root, item.sha256, item.byteSize);
      assets.set(item.sha256, asset);
      totalBytes += item.byteSize;
    } else if (asset.length !== item.byteSize) {
      throw new Error("Same-byte corpus maps one image hash to inconsistent byte sizes.");
    }
    if (!SHA256_PATTERN.test(item.urlSha256 || "") || entriesByUrlHash.has(item.urlSha256)) {
      throw new Error("Same-byte corpus contains a duplicate or invalid image URL key.");
    }
    entriesByUrlHash.set(item.urlSha256, item);
  }
  if (totalBytes > MAX_CORPUS_ASSET_BYTES) {
    throw new Error("Same-byte corpus exceeds its aggregate image byte limit.");
  }
  const imageRoutesByIndex = new Map();
  for (const route of manifest.imageRoutes) {
    if (!Number.isSafeInteger(route.index) || route.index < 0 || route.index >= MAX_ASSET_LOOKUP_ENTRIES ||
        imageRoutesByIndex.has(route.index)) {
      throw new Error("Same-byte corpus contains a duplicate or invalid rendered image route.");
    }
    if (route.dataUri === true) {
      if (route.urlSha256 !== undefined || route.sha256 !== undefined || route.mimeType !== undefined) {
        throw new Error("Same-byte data-image routes must not contain an external URL or asset mapping.");
      }
    } else {
      const entry = entriesByUrlHash.get(route.urlSha256);
      if (!SHA256_PATTERN.test(route.urlSha256 || "") || !SHA256_PATTERN.test(route.sha256 || "") ||
          !isImageMime(route.mimeType) || !entry || entry.sha256 !== route.sha256 ||
          entry.mimeType !== route.mimeType || !assets.has(route.sha256)) {
        throw new Error("Same-byte corpus contains an unbound or invalid rendered image route.");
      }
    }
    imageRoutesByIndex.set(route.index, route);
  }
  if (manifest.browserRender.status === "not-applicable" &&
      (manifest.imageRoutes.length !== 0 || manifest.assets.length !== 0)) {
    throw new Error("A GitHub source-view corpus cannot contain browser-rendered image routes.");
  }
  const assetUrlMapSha256 = sha256(Buffer.from(JSON.stringify(manifest.assets), "utf8"));
  return {
    root,
    manifest,
    manifestBytes,
    manifestSha256: sha256(manifestBytes),
    readmeBytes,
    renderedHtmlBytes,
    assets,
    entriesByUrlHash,
    imageRoutesByIndex,
    assetUrlMapSha256,
  };
}

export async function createSameByteReplayServer(corpusDirectory) {
  const corpus = await loadSameByteCorpus(corpusDirectory);
  const markedParserBytes = await readFile(MARKED_PARSER_PATH);
  if (sha256(markedParserBytes) !== PINNED_GFM_PARSER.sha256) {
    throw new Error("Vendored Marked parser does not match the pinned SHA-256.");
  }
  const styleProfileBytes = await readBoundedFile(GITHUB_PRIMER_LIGHT_V4_CSS_PATH, 64 * 1024);
  if (sha256(styleProfileBytes) !== PINNED_GITHUB_README_STYLE_PROFILE.sha256) {
    throw new Error("Canonical GitHub README style profile does not match its pinned SHA-256.");
  }
  const emojiShortcodes = await loadPinnedGitHubEmojiShortcodes();
  let markedParserRequests = 0;
  let emojiExtensionRequests = 0;
  let emojiMapRequests = 0;
  let styleProfileRequests = 0;
  let misses = 0;
  let servedImageRoutes = 0;
  const servedUrlHashes = new Set();
  const server = http.createServer(async (request, response) => {
    const url = new URL(request.url || "/", "http://127.0.0.1");
    if (request.method !== "GET") {
      response.writeHead(405, { "content-type": "text/plain; charset=utf-8" }).end("Method Not Allowed");
      return;
    }
    if (url.pathname === PINNED_GITHUB_README_STYLE_PROFILE.cssPath) {
      styleProfileRequests++;
      response.writeHead(200, {
        "content-type": "text/css; charset=utf-8",
        "content-length": styleProfileBytes.length,
        "cache-control": "no-store",
        "x-content-sha256": PINNED_GITHUB_README_STYLE_PROFILE.sha256,
        "x-markdown-style-profile-id": PINNED_GITHUB_README_STYLE_PROFILE.id,
        "x-markdown-style-profile-version": String(PINNED_GITHUB_README_STYLE_PROFILE.version),
      }).end(styleProfileBytes);
      return;
    }
    if (url.pathname === "/replay") {
      if (!corpus.renderedHtmlBytes) {
        response.writeHead(409, { "content-type": "text/plain; charset=utf-8" }).end("This README has no GitHub rendered article.");
        return;
      }
      response.writeHead(200, {
        "content-type": "text/html; charset=utf-8",
        "cache-control": "no-store",
        "content-security-policy": "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; font-src 'self' data:; script-src 'none'; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'",
        "x-content-sha256": corpus.manifest.browserRender.sha256,
      }).end("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Offline README replay</title></head><body style=\"margin:0\"></body></html>");
      return;
    }
    if (url.pathname === "/source") {
      response.writeHead(200, {
        "content-type": "text/html; charset=utf-8",
        "cache-control": "no-store",
        "content-security-policy": "default-src 'none'; script-src 'self'; connect-src 'self'; img-src 'self' blob:; style-src 'self' 'unsafe-inline'; font-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
        "x-content-sha256": corpus.manifestSha256,
      }).end(sourceReplayHtml(
        `sha256-${Buffer.from(PINNED_GFM_PARSER.sha256, "hex").toString("base64")}`,
        `sha256-${Buffer.from(PINNED_GITHUB_EMOJI_PARSER.extensionSha256, "hex").toString("base64")}`,
        `sha256-${Buffer.from(PINNED_GITHUB_README_STYLE_PROFILE.sha256, "hex").toString("base64")}`,
      ));
      return;
    }
    if (url.pathname === "/marked-parser.js") {
      markedParserRequests++;
      response.writeHead(200, {
        "content-type": "application/javascript; charset=utf-8",
        "content-length": markedParserBytes.length,
        "cache-control": "no-store",
        "x-content-sha256": PINNED_GFM_PARSER.sha256,
      }).end(markedParserBytes);
      return;
    }
    if (url.pathname === "/github-emoji-shortcodes.js") {
      emojiExtensionRequests++;
      response.writeHead(200, {
        "content-type": "application/javascript; charset=utf-8",
        "content-length": emojiShortcodes.extensionBytes.length,
        "cache-control": "no-store",
        "x-content-sha256": PINNED_GITHUB_EMOJI_PARSER.extensionSha256,
      }).end(emojiShortcodes.extensionBytes);
      return;
    }
    if (url.pathname === "/github-emoji-shortcodes.json") {
      emojiMapRequests++;
      response.writeHead(200, {
        "content-type": "application/json; charset=utf-8",
        "content-length": emojiShortcodes.mapBytes.length,
        "cache-control": "no-store",
        "x-content-sha256": PINNED_GITHUB_EMOJI_PARSER.mapSha256,
      }).end(emojiShortcodes.mapBytes);
      return;
    }
    if (url.pathname === "/manifest") {
      response.writeHead(200, {
        "content-type": "application/json; charset=utf-8",
        "content-length": corpus.manifestBytes.length,
        "cache-control": "no-store",
        "x-content-sha256": corpus.manifestSha256,
      }).end(corpus.manifestBytes);
      return;
    }
    if (url.pathname === "/readme") {
      response.writeHead(200, {
        "content-type": "text/markdown; charset=utf-8",
        "content-length": corpus.readmeBytes.length,
        "cache-control": "no-store",
        "x-content-sha256": corpus.manifest.readme.sha256,
      }).end(corpus.readmeBytes);
      return;
    }
    if (url.pathname === "/rendered-html") {
      if (!corpus.renderedHtmlBytes) {
        response.writeHead(409, { "content-type": "text/plain; charset=utf-8" })
          .end("This README has no GitHub rendered article.");
        return;
      }
      response.writeHead(200, {
        "content-type": "text/html; charset=utf-8",
        "content-length": corpus.renderedHtmlBytes.length,
        "cache-control": "no-store",
        "x-content-sha256": corpus.manifest.browserRender.sha256,
      }).end(corpus.renderedHtmlBytes);
      return;
    }
    if (url.pathname === "/asset") {
      const sourceUrl = url.searchParams.get("url");
      let sourceHash = "";
      try { if (sourceUrl) sourceHash = resourceUrlSha256(sourceUrl); } catch {}
      const entry = sourceHash && corpus.entriesByUrlHash.get(sourceHash);
      const digest = entry?.sha256;
      const bytes = digest && corpus.assets.get(digest);
      if (bytes) {
        response.writeHead(200, {
          "content-type": entry.mimeType,
          "content-length": bytes.length,
          "cache-control": "no-store",
          "x-content-sha256": digest,
        }).end(bytes);
        servedImageRoutes++;
        servedUrlHashes.add(sourceHash);
        return;
      }
    }
    const contentRouteMatch = /^\/asset-by-content-sha256\/([0-9a-f]{64})$/u.exec(url.pathname);
    if (contentRouteMatch) {
      const rawRouteIndex = url.searchParams.get("index") || "";
      const routeIndex = /^(?:0|[1-9][0-9]*)$/u.test(rawRouteIndex) ? Number(rawRouteIndex) : -1;
      const route = corpus.imageRoutesByIndex.get(routeIndex);
      const digest = contentRouteMatch[1];
      const bytes = route && route.dataUri !== true && route.sha256 === digest
        ? corpus.assets.get(digest)
        : null;
      if (bytes) {
        response.writeHead(200, {
          "content-type": route.mimeType,
          "content-length": bytes.length,
          "cache-control": "no-store",
          "x-content-sha256": digest,
        }).end(bytes);
        servedImageRoutes++;
        servedUrlHashes.add(route.urlSha256);
        return;
      }
    }
    const urlHashRouteMatch = /^\/asset-by-url-sha256\/([0-9a-f]{64})$/u.exec(url.pathname);
    if (urlHashRouteMatch) {
      const urlHash = urlHashRouteMatch[1];
      const entry = corpus.entriesByUrlHash.get(urlHash);
      const digest = entry?.sha256;
      const bytes = digest && corpus.assets.get(digest);
      if (entry && bytes) {
        response.writeHead(200, {
          "content-type": entry.mimeType,
          "content-length": bytes.length,
          "cache-control": "no-store",
          "x-content-sha256": digest,
        }).end(bytes);
        servedImageRoutes++;
        servedUrlHashes.add(urlHash);
        return;
      }
    }
    if (url.pathname === "/favicon.ico") {
      response.writeHead(204, { "cache-control": "no-store" }).end();
      return;
    }
    misses++;
    response.writeHead(404, {
      "content-type": "text/plain; charset=utf-8",
      "cache-control": "no-store",
      "x-same-byte-replay": "miss",
    }).end("No captured response exists for this request.");
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  if (!address || typeof address === "string" || address.address !== "127.0.0.1") {
    await closeServer(server);
    throw new Error("Same-byte replay server did not bind to IPv4 loopback.");
  }
  return {
    baseUrl: `http://127.0.0.1:${address.port}`,
    manifestSha256: corpus.manifestSha256,
    manifest: corpus.manifest,
    manifestBytes: corpus.manifestBytes,
    readmeBytes: corpus.readmeBytes,
    renderedHtmlBytes: corpus.renderedHtmlBytes,
    readmeSha256: corpus.manifest.readme.sha256,
    readmeGitBlobSha1: corpus.manifest.repository.readmeGitBlobSha1,
    renderedHtmlSha256: corpus.manifest.browserRender.sha256 || "",
    assetUrlMapSha256: corpus.assetUrlMapSha256,
    imageRoutes: corpus.manifest.imageRoutes,
    expectedVisibleImageCount: corpus.imageRoutesByIndex.size,
    expectedUrlHashes: [...new Set(corpus.manifest.imageRoutes.filter(route => route.dataUri !== true).map(route => route.urlSha256))].sort(),
    parser: { ...PINNED_SOURCE_REPLAY_PARSER },
    styleProfile: { ...PINNED_GITHUB_README_STYLE_PROFILE },
    get servedParserCount() { return markedParserRequests; },
    get servedEmojiExtensionCount() { return emojiExtensionRequests; },
    get servedEmojiMapCount() { return emojiMapRequests; },
    get servedStyleProfileCount() { return styleProfileRequests; },
    get misses() { return misses; },
    get servedImageRoutes() { return servedImageRoutes; },
    get servedUrlSha256s() { return [...servedUrlHashes].sort(); },
    close: () => closeServer(server),
  };
}

function sourceReplayHtml(parserIntegrity, emojiExtensionIntegrity, styleProfileIntegrity) {
  return `<!doctype html>
<html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Source-bound GFM replay</title>
<link rel="stylesheet" href="${PINNED_GITHUB_README_STYLE_PROFILE.cssPath}" data-markdown-style-profile="${PINNED_GITHUB_README_STYLE_PROFILE.id}" data-markdown-style-profile-version="${PINNED_GITHUB_README_STYLE_PROFILE.version}" integrity="${styleProfileIntegrity}" crossorigin="anonymous">
<script defer src="/marked-parser.js" integrity="${parserIntegrity}" crossorigin="anonymous"></script><script defer src="/github-emoji-shortcodes.js" integrity="${emojiExtensionIntegrity}" crossorigin="anonymous"></script></head><body><main id="readme" class="markdown-body"></main></body></html>`;
}

async function fetchResolvedPinnedReadme({
  repository,
  readmeGitBlobSha1,
  readmeByteSize,
  githubToken,
  fetchImpl,
}) {
  const fullName = repository.fullName;
  const commitSha = repository.commitSha;
  if (typeof fullName !== "string" ||
      !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u.test(fullName) ||
      typeof commitSha !== "string" || !/^[0-9a-f]{40}$/iu.test(commitSha)) {
    throw new Error("Cannot resolve a mismatched README without a valid pinned GitHub repository and commit.");
  }
  if (typeof githubToken !== "string" || githubToken.trim().length === 0 ||
      githubToken.length > 4096 || /[\r\n]/u.test(githubToken)) {
    throw new Error("Fetched README bytes do not match the pinned GitHub blob identity; authenticated fallback is unavailable.");
  }

  const [owner, repo] = fullName.split("/");
  const apiUrl = new URL(
    `/repos/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/readme`,
    "https://api.github.com",
  );
  apiUrl.searchParams.set("ref", commitSha.toLowerCase());
  const response = await fetchImpl(apiUrl, {
    headers: {
      accept: "application/vnd.github+json",
      authorization: `Bearer ${githubToken.trim()}`,
      "user-agent": "JitHub-Readme-Same-Byte-Capture",
      "x-github-api-version": "2022-11-28",
    },
    redirect: "error",
    signal: AbortSignal.timeout(PINNED_README_FETCH_TIMEOUT_MS),
  });
  if (!response.ok || !response.body) {
    throw new Error(`Pinned GitHub README Contents API request failed with HTTP ${response.status}.`);
  }
  if (response.url) {
    const finalUrl = new URL(response.url);
    if (finalUrl.protocol !== "https:" || finalUrl.hostname.toLowerCase() !== "api.github.com" ||
        finalUrl.pathname !== apiUrl.pathname || finalUrl.searchParams.get("ref") !== commitSha.toLowerCase()) {
      throw new Error("Pinned GitHub README Contents API response came from an unexpected endpoint.");
    }
  }

  const responseBytes = await readBoundedResponse(
    response,
    MAX_README_API_RESPONSE_BYTES,
    "Pinned GitHub README API",
  );
  let document;
  try {
    document = JSON.parse(responseBytes.toString("utf8"));
  } catch {
    throw new Error("Pinned GitHub README Contents API response is malformed.");
  }
  if (document?.type !== "file" ||
      typeof document.path !== "string" || !isSafeRepositoryPath(document.path) ||
      document.encoding !== "base64" ||
      typeof document.content !== "string" ||
      typeof document.sha !== "string" || document.sha.toLowerCase() !== readmeGitBlobSha1 ||
      document.size !== readmeByteSize) {
    throw new Error("Pinned GitHub README Contents API metadata does not match the expected README blob.");
  }

  const base64 = document.content.replace(/[ \t\r\n]/gu, "");
  const maximumBase64Characters = Math.ceil(MAX_README_BYTES / 3) * 4;
  if (base64.length > maximumBase64Characters ||
      !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(base64)) {
    throw new Error("Pinned GitHub README Contents API payload is not bounded canonical base64.");
  }
  const readmeBytes = Buffer.from(base64, "base64");
  if (readmeBytes.length !== readmeByteSize ||
      readmeBytes.length > MAX_README_BYTES ||
      readmeBytes.toString("base64") !== base64 ||
      gitBlobSha1(readmeBytes) !== readmeGitBlobSha1) {
    throw new Error("Resolved GitHub README bytes do not match the pinned GitHub blob identity.");
  }
  return readmeBytes;
}

function isSafeRepositoryPath(value) {
  return value.length > 0 && value.length <= 4096 &&
    !value.startsWith("/") && !value.includes("\\") && !value.includes("\0") &&
    value.split("/").every(segment => segment.length > 0 && segment !== "." && segment !== "..");
}

export function gitBlobSha1(bytes) {
  const content = Buffer.from(bytes);
  const header = Buffer.from(`blob ${content.length}\0`, "utf8");
  return createHash("sha1").update(header).update(content).digest("hex");
}

export function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

function validateManifestShape(manifest) {
  const browserRenderValid = manifest?.browserRender?.status === "passed"
    ? manifest.browserRender.file === "rendered.html" &&
      Number.isSafeInteger(manifest.browserRender.byteSize) &&
      manifest.browserRender.byteSize > 0 && manifest.browserRender.byteSize <= MAX_RENDERED_HTML_BYTES &&
      SHA256_PATTERN.test(manifest.browserRender.sha256 || "")
    : manifest?.browserRender?.status === "not-applicable" &&
      manifest.browserRender.reason === "github-source-view";
  if (manifest?.schemaVersion !== 2 || manifest.complete !== true || !browserRenderValid ||
      !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u.test(manifest.repository?.fullName || "") ||
      !/^[0-9a-f]{40}$/iu.test(manifest.repository?.commitSha || "") ||
      !isGitSha1(manifest.repository?.readmeGitBlobSha1) ||
      typeof manifest.repository?.readmePath !== "string" || !manifest.repository.readmePath ||
      manifest.readme?.file !== "readme.md" ||
      !Number.isSafeInteger(manifest.readme?.byteSize) ||
      manifest.readme.byteSize < 0 || manifest.readme.byteSize > MAX_README_BYTES ||
      !SHA256_PATTERN.test(manifest.readme?.sha256 || "") ||
      !Array.isArray(manifest.assets) ||
      manifest.assets.length > MAX_ASSET_LOOKUP_ENTRIES ||
      !Array.isArray(manifest.imageRoutes) ||
      manifest.imageRoutes.length > MAX_ASSET_LOOKUP_ENTRIES ||
      (manifest.nonRenderedDecorativeImageExclusions !== undefined &&
       !isValidNonRenderedDecorativeImageExclusionList(manifest.nonRenderedDecorativeImageExclusions)) ||
      manifest.limits?.maxReadmeBytes !== MAX_README_BYTES ||
      manifest.limits?.maxRenderedHtmlBytes !== MAX_RENDERED_HTML_BYTES ||
      manifest.limits?.maxAssetBytes !== MAX_ASSET_BYTES ||
      manifest.limits?.maxAggregateAssetBytes !== MAX_CORPUS_ASSET_BYTES) {
    throw new Error("Same-byte corpus manifest is incomplete, unsupported, or malformed.");
  }
  if (manifest.browserRender.status === "not-applicable" &&
      (Object.hasOwn(manifest.browserRender, "file") || Object.hasOwn(manifest.browserRender, "sha256") ||
       Object.hasOwn(manifest.browserRender, "byteSize"))) {
    throw new Error("A source-view corpus cannot claim a rendered HTML snapshot.");
  }
  if (manifest.browserRender.status === "not-applicable" &&
      (manifest.nonRenderedDecorativeImageExclusions?.length || 0) > 0) {
    throw new Error("A GitHub source-view corpus cannot contain rendered decorative-image exclusions.");
  }
}

function isValidNonRenderedDecorativeImageExclusionList(value) {
  if (!Array.isArray(value) || value.length > MAX_NON_RENDERED_DECORATIVE_IMAGE_EXCLUSIONS) return false;
  return value.every(item => {
    if (!item || item.explicitEmptyAlt !== true || item.failedDecode !== true || item.zeroBounds !== true ||
        !Array.isArray(item.urlSha256s) || item.urlSha256s.length < 1 ||
        item.urlSha256s.length > MAX_DECORATIVE_IMAGE_URL_HASHES) {
      return false;
    }
    const hashes = new Set();
    for (const hash of item.urlSha256s) {
      if (!SHA256_PATTERN.test(hash || "") || hashes.has(hash.toLowerCase())) return false;
      hashes.add(hash.toLowerCase());
    }
    return true;
  });
}

function validateRenderedSnapshot(html, imageRoutes) {
  if (typeof html !== "string" || !/^<article\b/iu.test(html)) {
    rejectRenderedSnapshot("invalid-root", "Same-byte rendered HTML has an invalid root.");
  }
  const elements = scanSerializedHtmlElements(html);
  const activeElement = elements.find(element => SNAPSHOT_ACTIVE_TAGS.has(element.tagName));
  if (activeElement) {
    rejectRenderedSnapshot("active-element-tag", "Same-byte rendered HTML contains an active element.", {
      elementTag: activeElement.tagName,
    });
  }
  const stylesheetLink = elements.find(element => element.tagName === "link" &&
    (element.attributes.get("rel") || "").toLowerCase().split(/\s+/u).includes("stylesheet"));
  if (stylesheetLink) {
    rejectRenderedSnapshot("stylesheet-link", "Same-byte rendered HTML contains a stylesheet link.", {
      elementTag: stylesheetLink.tagName,
      attributeName: "rel",
    });
  }
  for (const element of elements) {
    const eventAttribute = [...element.attributes.keys()].find(name => name.startsWith("on"));
    if (eventAttribute) {
      rejectRenderedSnapshot("event-handler-attribute", "Same-byte rendered HTML contains an event-handler attribute.", {
        elementTag: element.tagName,
        attributeName: eventAttribute,
      });
    }
  }
  for (const element of elements) {
    const resourceAttribute = [...element.attributes.keys()].find(name => SNAPSHOT_RESOURCE_ATTRIBUTES.has(name));
    if (resourceAttribute) {
      rejectRenderedSnapshot("external-resource-attribute", "Same-byte rendered HTML contains a live resource attribute.", {
        elementTag: element.tagName,
        attributeName: resourceAttribute,
      });
    }
  }
  const styleElement = elements.find(element => element.tagName === "style");
  if (styleElement) {
    rejectRenderedSnapshot("style-element", "Same-byte rendered HTML contains a style element.", {
      elementTag: styleElement.tagName,
    });
  }
  for (const element of elements) {
    const style = element.attributes.get("style");
    if (style !== undefined && containsExternalCssResource(style)) {
      rejectRenderedSnapshot("external-css-resource", "Same-byte rendered HTML contains an external CSS resource.", {
        elementTag: element.tagName,
        attributeName: "style",
      });
    }
  }
  const imagesByIndex = new Map();
  for (const element of elements) {
    if (element.tagName !== "img") continue;
    const indexValue = element.attributes.get("data-jithub-image-index");
    if (!/^\d+$/u.test(indexValue || "")) continue;
    const index = Number(indexValue);
    if (!Number.isSafeInteger(index) || imagesByIndex.has(index)) {
      rejectRenderedSnapshot("duplicate-image-index", "Same-byte rendered HTML contains a duplicate or invalid image index.", {
        elementTag: element.tagName,
        attributeName: "data-jithub-image-index",
      });
    }
    imagesByIndex.set(index, element.attributes.get("data-jithub-image-data") || "");
  }
  for (const route of imageRoutes) {
    if (!imagesByIndex.has(route.index)) {
      rejectRenderedSnapshot("missing-image-index", "Same-byte rendered HTML is missing an indexed captured visible image.");
    }
    const dataUri = imagesByIndex.get(route.index);
    if (route.dataUri === true) {
      if (!/^data:image\/[a-z0-9.+-]+(?:;[^,]*)?,/iu.test(dataUri)) {
        rejectRenderedSnapshot("missing-data-image", "Same-byte rendered HTML is missing the selected data image bytes.");
      }
    } else if (dataUri) {
      rejectRenderedSnapshot("network-image-data-payload", "A network image route cannot also contain a data-image payload.");
    }
  }
}

function scanSerializedHtmlElements(html) {
  const elements = [];
  let cursor = 0;
  while (cursor < html.length) {
    const open = html.indexOf("<", cursor);
    if (open < 0) break;
    if (html.startsWith("<!--", open)) {
      const commentEnd = html.indexOf("-->", open + 4);
      if (commentEnd < 0) {
        rejectRenderedSnapshot("invalid-root", "Same-byte rendered HTML contains an unterminated comment.");
      }
      cursor = commentEnd + 3;
      continue;
    }
    if (html[open + 1] === "!" || html[open + 1] === "?") {
      rejectRenderedSnapshot("invalid-root", "Same-byte rendered HTML contains an unsupported declaration.");
    }
    if (html[open + 1] === "/") {
      const close = html.indexOf(">", open + 2);
      if (close < 0) rejectRenderedSnapshot("invalid-root", "Same-byte rendered HTML contains an unterminated closing tag.");
      cursor = close + 1;
      continue;
    }
    const initial = html[open + 1];
    if (!initial || !/[a-z]/iu.test(initial)) {
      cursor = open + 1;
      continue;
    }

    let position = open + 2;
    while (position < html.length && /[a-z0-9:_-]/iu.test(html[position])) position++;
    const tagName = html.slice(open + 1, position).toLowerCase();
    const attributes = new Map();
    let tagClosed = false;
    while (position < html.length) {
      while (position < html.length && /[\t\n\f\r ]/u.test(html[position])) position++;
      if (html[position] === ">") {
        position++;
        tagClosed = true;
        break;
      }
      if (html[position] === "/" && html[position + 1] === ">") {
        position += 2;
        tagClosed = true;
        break;
      }
      const attributeStart = position;
      while (position < html.length && !/[\t\n\f\r =>]/u.test(html[position])) position++;
      if (position === attributeStart) {
        rejectRenderedSnapshot("invalid-root", "Same-byte rendered HTML contains a malformed start tag.");
      }
      const attributeName = html.slice(attributeStart, position).toLowerCase();
      while (position < html.length && /[\t\n\f\r ]/u.test(html[position])) position++;
      let attributeValue = "";
      if (html[position] === "=") {
        position++;
        while (position < html.length && /[\t\n\f\r ]/u.test(html[position])) position++;
        const quote = html[position];
        if (quote === "\"" || quote === "'") {
          position++;
          const valueStart = position;
          while (position < html.length && html[position] !== quote) position++;
          if (position >= html.length) {
            rejectRenderedSnapshot("invalid-root", "Same-byte rendered HTML contains an unterminated attribute.");
          }
          attributeValue = html.slice(valueStart, position);
          position++;
        } else {
          const valueStart = position;
          while (position < html.length && !/[\t\n\f\r >]/u.test(html[position])) position++;
          attributeValue = html.slice(valueStart, position);
        }
      }
      if (!attributes.has(attributeName)) attributes.set(attributeName, attributeValue);
    }
    if (!tagClosed) rejectRenderedSnapshot("invalid-root", "Same-byte rendered HTML contains an unterminated start tag.");
    elements.push({ tagName, attributes });
    cursor = position;
  }
  return elements;
}

function rejectRenderedSnapshot(predicate, message, detail = {}) {
  const error = new Error(message);
  const elementTag = detail.elementTag;
  const attributeName = detail.attributeName;
  Object.defineProperties(error, {
    sameByteSnapshotFailureStage: { value: "rendered-snapshot-validation" },
    sameByteSnapshotFailurePredicate: { value: predicate },
    sameByteSnapshotElementTag: {
      value: SNAPSHOT_DIAGNOSTIC_TAGS.has(elementTag) ? elementTag : undefined,
    },
    sameByteSnapshotAttributeName: {
      value: SNAPSHOT_DIAGNOSTIC_ATTRIBUTES.has(attributeName) ? attributeName : undefined,
    },
  });
  throw error;
}

export function readSameByteSnapshotFailureEvidence(error) {
  if (error?.sameByteSnapshotFailureStage !== "rendered-snapshot-validation" ||
      !RENDERED_SNAPSHOT_FAILURE_PREDICATES.has(error?.sameByteSnapshotFailurePredicate)) {
    return null;
  }
  const evidence = {
    failureStage: "rendered-snapshot-validation",
    failurePredicate: error.sameByteSnapshotFailurePredicate,
  };
  if (SNAPSHOT_DIAGNOSTIC_TAGS.has(error.sameByteSnapshotElementTag)) {
    evidence.elementTag = error.sameByteSnapshotElementTag;
  }
  if (SNAPSHOT_DIAGNOSTIC_ATTRIBUTES.has(error.sameByteSnapshotAttributeName)) {
    evidence.attributeName = error.sameByteSnapshotAttributeName;
  }
  return evidence;
}

export function createCapturedImageAltIdentities(renderedHtmlBytes, imageRoutes) {
  if (!Buffer.isBuffer(renderedHtmlBytes) || !Array.isArray(imageRoutes)) return [];
  let elements;
  try {
    elements = scanSerializedHtmlElements(renderedHtmlBytes.toString("utf8"));
  } catch {
    return [];
  }
  const imagesByIndex = new Map();
  for (const element of elements) {
    if (element.tagName !== "img") continue;
    const indexValue = element.attributes.get("data-jithub-image-index") || "";
    if (!/^(?:0|[1-9][0-9]*)$/u.test(indexValue)) continue;
    const index = Number(indexValue);
    if (!Number.isSafeInteger(index) || imagesByIndex.has(index)) return [];
    imagesByIndex.set(index, element);
  }
  if (imagesByIndex.size !== imageRoutes.length) return [];
  const identities = [];
  for (const route of imageRoutes) {
    const image = imagesByIndex.get(route.index);
    if (!image) return [];
    const rawAlt = image.attributes.get("alt");
    const alt = rawAlt === undefined ? null : decodeSerializedHtmlAttribute(rawAlt);
    identities.push({
      index: route.index,
      altSha256: alt && alt.length > 0
        ? sha256(Buffer.from(alt.normalize("NFC"), "utf8"))
        : null,
    });
  }
  return identities;
}

function decodeSerializedHtmlAttribute(value) {
  let invalid = false;
  const decoded = value.replace(/&(?:#(?:x[0-9a-f]+|[0-9]+)|amp|lt|gt|quot|apos);/giu, reference => {
    const body = reference.slice(1, -1);
    if (body[0] === "#") {
      const hexadecimal = body[1]?.toLowerCase() === "x";
      const codePoint = Number.parseInt(body.slice(hexadecimal ? 2 : 1), hexadecimal ? 16 : 10);
      if (!Number.isSafeInteger(codePoint) || codePoint <= 0 || codePoint > 0x10ffff ||
          (codePoint >= 0xd800 && codePoint <= 0xdfff)) {
        invalid = true;
        return "";
      }
      return String.fromCodePoint(codePoint);
    }
    return ({
      amp: "&",
      apos: "'",
      gt: ">",
      lt: "<",
      quot: "\"",
    })[body.toLowerCase()];
  });
  if (invalid || /&(?:#(?:x[0-9a-f]+|[0-9]+)|[a-z][a-z0-9]+);/iu.test(decoded)) return null;
  return decoded;
}

function containsExternalCssResource(value) {
  if (/@import/iu.test(value || "")) return true;
  for (const match of (value || "").matchAll(/url\s*\(\s*(?:(["'])(.*?)\1|([^)]+))\s*\)/giu)) {
    const reference = (match[2] ?? match[3] ?? "").trim();
    if (!reference.startsWith("#")) return true;
  }
  return false;
}

async function loadAsset(root, digest, expectedSize) {
  const assetPath = path.join(root, "assets", digest);
  const bytes = await readBoundedFile(assetPath, MAX_ASSET_BYTES);
  if (bytes.length !== expectedSize || sha256(bytes) !== digest) {
    throw new Error(`Same-byte corpus asset '${digest}' is missing or does not match its SHA-256.`);
  }
  return bytes;
}

async function readBoundedFile(filePath, maximumBytes) {
  const entry = await lstat(filePath);
  if (!entry.isFile() || entry.size < 0 || entry.size > maximumBytes) {
    throw new Error("Same-byte corpus file is missing, redirected, or exceeds its byte limit.");
  }

  const handle = await open(filePath, constants.O_RDONLY | (constants.O_NOFOLLOW || 0));
  try {
    const opened = await handle.stat();
    if (!opened.isFile() || opened.size < 0 || opened.size > maximumBytes) {
      throw new Error("Same-byte corpus file changed or exceeds its byte limit.");
    }
    const chunks = [];
    let totalBytes = 0;
    const buffer = Buffer.alloc(64 * 1024);
    while (true) {
      const { bytesRead } = await handle.read(buffer, 0, Math.min(buffer.length, maximumBytes - totalBytes + 1), null);
      if (bytesRead === 0) break;
      if (totalBytes + bytesRead > maximumBytes) {
        throw new Error("Same-byte corpus file exceeds its byte limit.");
      }
      totalBytes += bytesRead;
      chunks.push(Buffer.from(buffer.subarray(0, bytesRead)));
    }
    return Buffer.concat(chunks, totalBytes);
  } finally {
    await handle.close();
  }
}

function normalizeHttpUrl(value) {
  try {
    const url = new URL(value);
    if (!((url.protocol === "https:" || url.protocol === "http:") &&
          !url.username && !url.password)) return "";
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

export function resourceUrlSha256(value) {
  const normalized = normalizeHttpUrl(value);
  if (!normalized) throw new Error("Same-byte resource URL is invalid or contains credentials.");
  return sha256(Buffer.from(normalized, "utf8"));
}

function isImageMime(value) {
  return typeof value === "string" && CAPTURED_IMAGE_MIMES.has(value.toLowerCase());
}

function detectCapturedImageMime(bytes) {
  const value = Buffer.from(bytes);
  if (value.length >= 8 && value.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))) {
    return "image/png";
  }
  if (value.length >= 3 && value[0] === 0xff && value[1] === 0xd8 && value[2] === 0xff) {
    return "image/jpeg";
  }
  if (value.length >= 6 && /^GIF8[79]a$/u.test(value.subarray(0, 6).toString("ascii"))) {
    return "image/gif";
  }
  if (value.length >= 12 && value.toString("ascii", 0, 4) === "RIFF" &&
      value.toString("ascii", 8, 12) === "WEBP") {
    return "image/webp";
  }
  if (value.length >= 2 && value[0] === 0x42 && value[1] === 0x4d) {
    return "image/bmp";
  }
  if (value.length >= 4 && value[0] === 0x00 && value[1] === 0x00 &&
      value[2] === 0x01 && value[3] === 0x00) {
    return "image/x-icon";
  }
  if (hasAvifBrand(value)) return "image/avif";
  if (looksLikeSvg(value)) return "image/svg+xml";
  return "";
}

function hasAvifBrand(bytes) {
  if (bytes.length < 16 || bytes.toString("ascii", 4, 8) !== "ftyp") return false;
  const boxLength = bytes.readUInt32BE(0);
  if (boxLength < 16 || boxLength > bytes.length) return false;
  const brands = bytes.toString("ascii", 8, boxLength);
  return /(?:^|.{4})(?:avif|avis)(?:.{4}|$)/su.test(brands);
}

function looksLikeSvg(bytes) {
  const prefix = bytes.subarray(0, Math.min(bytes.length, 4096)).toString("utf8");
  // GitHub serves some valid, browser-rendered SVGs with an inert SVG 1.1
  // external doctype (for example the build-your-own-x badge). This is only
  // payload sniffing: the native SVG preflight still forbids entity expansion
  // and all nested external access, and the Edge replay blocks every request
  // outside its hash-bound loopback corpus.
  return /^\uFEFF?\s*(?:<\?xml\b[^>]*>\s*)?(?:<!--[\s\S]*?-->\s*)*(?:<!DOCTYPE\s+svg\b[^>]*>\s*(?:<!--[\s\S]*?-->\s*)*)?<svg(?:\s|>)/iu.test(prefix);
}

function isGitSha1(value) {
  return typeof value === "string" && GIT_SHA1_PATTERN.test(value);
}

function closeServer(server) {
  return new Promise((resolve, reject) => {
    server.close(error => error ? reject(error) : resolve());
    // Edge may retain keep-alive connections after all replay bytes have been
    // verified. Close existing sockets after refusing new ones, so cleanup
    // cannot hold a top-500 shard indefinitely.
    server.closeAllConnections();
  });
}

async function readBoundedResponse(response, maximumBytes, sourceLabel = "Pinned README") {
  const contentLength = Number(response.headers.get("content-length"));
  if (Number.isSafeInteger(contentLength) && contentLength > maximumBytes) {
    throw new Error(`${sourceLabel} response exceeds the ${maximumBytes}-byte safety limit.`);
  }
  let totalBytes = 0;
  const chunks = [];
  for await (const chunk of response.body) {
    const chunkSize = Number.isSafeInteger(chunk?.byteLength)
      ? chunk.byteLength
      : Buffer.byteLength(chunk);
    if (totalBytes + chunkSize > maximumBytes) {
      throw new Error(`${sourceLabel} response exceeds the ${maximumBytes}-byte safety limit.`);
    }
    const bytes = Buffer.from(chunk);
    totalBytes += bytes.length;
    chunks.push(bytes);
  }
  return Buffer.concat(chunks, totalBytes);
}
