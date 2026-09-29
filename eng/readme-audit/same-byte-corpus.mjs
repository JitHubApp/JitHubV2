import { createHash } from "node:crypto";
import { constants, existsSync } from "node:fs";
import { lstat, mkdir, open, readFile, writeFile } from "node:fs/promises";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";

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
export const PINNED_GFM_PARSER = Object.freeze({
  name: "marked",
  version: "18.0.5",
  license: "MIT",
  sha256: "2dc4769dfde29f51c7aca1a539c6407c789c8ea644cf8b7d01ded28a9c1d800b",
  npmIntegrity: "sha512-S6GcvALHg6K4ohtu4E7x0a1AqhAjp6cV8KhLSyN9qVapnzJkusVBxZRcIU9AeYsbe6P1hKDusSbEOzGyyuce6w==",
});
const MARKED_PARSER_PATH = path.join(
  path.dirname(fileURLToPath(import.meta.url)),
  "vendor",
  `marked-${PINNED_GFM_PARSER.version}.umd.js`,
);
const SHA256_PATTERN = /^[0-9a-f]{64}$/u;
const GIT_SHA1_PATTERN = /^[0-9a-f]{40}$/u;
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

export async function captureSameByteCorpus({
  directory,
  repository,
  readmeUrl,
  readmePath,
  readmeGitBlobSha1,
  readmeByteSize,
  images,
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
  if (!response.ok || !response.body) {
    throw new Error(`Pinned README fetch failed with HTTP ${response.status}.`);
  }
  let readmeBytes = await readBoundedResponse(response, MAX_README_BYTES);
  if (readmeBytes.length !== readmeByteSize ||
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
  const renderedHtmlBytes = readmeRendered ? Buffer.from(renderedHtml, "utf8") : null;
  if (renderedHtmlBytes && (renderedHtmlBytes.length === 0 || renderedHtmlBytes.length > MAX_RENDERED_HTML_BYTES)) {
    throw new Error(`Rendered GitHub HTML exceeds the ${MAX_RENDERED_HTML_BYTES}-byte safety limit or is empty.`);
  }

  const captured = await responseRecorder.captureVisibleImages(images);
  const assets = captured.assets;
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
    readmeBytes: readmeBytes.length,
    readmeSha256: sha256(readmeBytes),
    browserRender: manifest.browserRender,
    renderedHtmlBytes,
    assetUrlMapSha256: sha256(Buffer.from(JSON.stringify(assetEntries), "utf8")),
    manifestSha256: sha256(manifestBytes),
  };
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
      record = { requestId: event.requestId, urls: new Set(), status: 0, mimeType: "", type: "", finished: false };
      requests.set(event.requestId, record);
    }
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
    record.status = event.response.status;
    record.mimeType = event.response.mimeType || "";
    record.type = event.type || "";
  });
  addListener("Network.loadingFinished", event => {
    const record = requests.get(event.requestId);
    if (!record) return;
    record.finished = true;
    record.encodedDataLength = event.encodedDataLength;
  });
  addListener("Network.loadingFailed", event => {
    const record = requests.get(event.requestId);
    if (record) record.failure = event.errorText || "Network.loadingFailed";
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
  let markedParserRequests = 0;
  let misses = 0;
  let servedImageRoutes = 0;
  const servedUrlHashes = new Set();
  const server = http.createServer(async (request, response) => {
    const url = new URL(request.url || "/", "http://127.0.0.1");
    if (request.method !== "GET") {
      response.writeHead(405, { "content-type": "text/plain; charset=utf-8" }).end("Method Not Allowed");
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
        "content-security-policy": "default-src 'none'; script-src 'self'; connect-src 'self'; img-src 'self' blob:; style-src 'unsafe-inline'; font-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
        "x-content-sha256": corpus.manifestSha256,
      }).end(sourceReplayHtml(`sha256-${Buffer.from(PINNED_GFM_PARSER.sha256, "hex").toString("base64")}`));
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
    parser: { ...PINNED_GFM_PARSER },
    get servedParserCount() { return markedParserRequests; },
    get misses() { return misses; },
    get servedImageRoutes() { return servedImageRoutes; },
    get servedUrlSha256s() { return [...servedUrlHashes].sort(); },
    close: () => closeServer(server),
  };
}

function sourceReplayHtml(parserIntegrity) {
  return `<!doctype html>
<html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Source-bound GFM replay</title>
<style>
:root{color-scheme:light;--readme-fg:#1f2328;--readme-bg:#fff;--readme-muted:#59636e;--readme-border:#d1d9e0;--readme-link:#0969da;--readme-code:#f6f8fa}
:root[data-color-scheme="dark"]{color-scheme:dark;--readme-fg:#e6edf3;--readme-bg:#0d1117;--readme-muted:#9198a1;--readme-border:#3d444d;--readme-link:#4493f8;--readme-code:#151b23}
html,body{margin:0;min-height:100%;background:var(--readme-bg);color:var(--readme-fg);font:14px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",Helvetica,Arial,sans-serif}
/* Primer's Markdown body uses its 16px h4 token, not the 14px page-body token. */
.markdown-body{box-sizing:border-box;width:100%;max-width:none;margin:0;padding:0;color:var(--readme-fg);font-size:16px;line-height:1.5;overflow-wrap:break-word}
.markdown-body>*:first-child{margin-top:0!important}.markdown-body>*:last-child{margin-bottom:0!important}
.markdown-body h1,.markdown-body h2{padding-bottom:.3em;border-bottom:1px solid var(--readme-border)}
.markdown-body h1{font-size:2em}.markdown-body h2{font-size:1.5em}.markdown-body h3{font-size:1.25em}.markdown-body h4{font-size:1em}.markdown-body h5{font-size:.875em}.markdown-body h6{font-size:.85em;color:var(--readme-muted)}
.markdown-body h1,.markdown-body h2,.markdown-body h3,.markdown-body h4,.markdown-body h5,.markdown-body h6{font-weight:600;line-height:1.25;margin:24px 0 16px}
.markdown-body p,.markdown-body blockquote,.markdown-body ul,.markdown-body ol,.markdown-body dl,.markdown-body table,.markdown-body pre{margin:0 0 16px}
.markdown-body ul,.markdown-body ol{padding-left:2em}.markdown-body li+li{margin-top:.25em}
.markdown-body a{color:var(--readme-link);text-decoration:none}.markdown-body code,.markdown-body pre{font-family:ui-monospace,SFMono-Regular,Consolas,"Liberation Mono",monospace}.markdown-body code{padding:.2em .4em;background:var(--readme-code);border-radius:6px;font-size:85%}.markdown-body pre{padding:16px;overflow:auto;background:var(--readme-code);border-radius:6px;font-size:85%;line-height:1.45}.markdown-body pre code{padding:0;background:transparent;font-size:100%}
.markdown-body blockquote{padding:0 1em;color:var(--readme-muted);border-left:.25em solid var(--readme-border)}
.markdown-body table{border-spacing:0;border-collapse:collapse;display:block;max-width:100%;overflow:auto}.markdown-body th,.markdown-body td{padding:6px 13px;border:1px solid var(--readme-border)}.markdown-body th{font-weight:600}.markdown-body tr:nth-child(2n){background:color-mix(in srgb,var(--readme-code) 60%,var(--readme-bg))}
.markdown-body img{max-width:100%;vertical-align:middle}.markdown-body hr{height:.25em;padding:0;background:var(--readme-border);border:0}.markdown-body input[type="checkbox"]{margin:0 .25em 0 -1.5em;vertical-align:middle}
</style><script defer src="/marked-parser.js" integrity="${parserIntegrity}" crossorigin="anonymous"></script></head><body><main id="readme" class="markdown-body"></main></body></html>`;
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
