import { createHash } from "node:crypto";
import { constants, existsSync } from "node:fs";
import { lstat, mkdir, open, writeFile } from "node:fs/promises";
import http from "node:http";
import path from "node:path";

const MAX_MANIFEST_BYTES = 4 * 1024 * 1024;
const MAX_README_BYTES = 16 * 1024 * 1024;
const MAX_ASSET_BYTES = 64 * 1024 * 1024;
const MAX_CORPUS_ASSET_BYTES = 256 * 1024 * 1024;
const MAX_TRACKED_NETWORK_REQUESTS = 100_000;
const MAX_TRACKED_URL_BYTES = 16 * 1024 * 1024;
const MAX_ASSET_LOOKUP_ENTRIES = 15_000;
const SHA256_PATTERN = /^[0-9a-f]{64}$/u;
const GIT_SHA1_PATTERN = /^[0-9a-f]{40}$/u;

export async function captureSameByteCorpus({
  directory,
  repository,
  readmeUrl,
  readmePath,
  readmeGitBlobSha1,
  readmeByteSize,
  images,
  responseRecorder,
  fetchImpl = fetch,
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
  const readmeBytes = await readBoundedResponse(response, MAX_README_BYTES);
  if (readmeBytes.length !== readmeByteSize ||
      gitBlobSha1(readmeBytes) !== readmeGitBlobSha1.toLowerCase()) {
    throw new Error("Fetched README bytes do not match the pinned GitHub blob identity.");
  }

  const assets = await responseRecorder.captureVisibleImages(images);
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

  const output = path.resolve(directory);
  await mkdir(path.join(output, "assets"), { recursive: true });
  await writeFile(path.join(output, "readme.md"), readmeBytes);
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
    schemaVersion: 1,
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
    limits: {
      maxReadmeBytes: MAX_README_BYTES,
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
      for (const image of images) {
        if (!image.complete || image.naturalWidth <= 0 || image.naturalHeight <= 0) {
          throw new Error("A browser-visible image was not fully decoded during same-byte capture.");
        }
        const aliases = [...new Set([image.currentSource, image.source, image.canonicalSource]
          .filter(value => typeof value === "string" && value.length > 0))];
        const selectedUrl = image.currentSource || image.source;
        if (!selectedUrl) throw new Error("A visible browser image has no selected source URL.");
        if (selectedUrl.startsWith("data:")) continue;
        const selectedKey = normalizeHttpUrl(selectedUrl);
        if (!selectedKey) throw new Error("A visible browser image has an unsupported resource URL.");
        const aliasKeys = aliases.map(normalizeHttpUrl).filter(Boolean);
        const existingAliases = selected.get(selectedKey) ?? new Set();
        for (const aliasKey of aliasKeys) existingAliases.add(aliasKey);
        selected.set(selectedKey, existingAliases);
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
      for (const [requestedUrl, imageAliases] of selected) {
        const matches = requestsByUrl.get(requestedUrl) ?? [];
        if (matches.length === 0) {
          throw new Error(`No captured network response matches visible image key ${resourceUrlSha256(requestedUrl)}.`);
        }
        const successful = matches.filter(record => record.status >= 200 && record.status < 300 && record.finished);
        if (successful.length === 0) {
          throw new Error(`Visible image key ${resourceUrlSha256(requestedUrl)} has no completed successful response.`);
        }
        const aliases = new Set(imageAliases);
        for (const record of matches) {
          for (const key of record.normalizedUrls) aliases.add(key);
        }
        for (const key of aliases) lookupKeys.add(key);
        if (lookupKeys.size > MAX_ASSET_LOOKUP_ENTRIES) {
          throw new Error("Same-byte corpus exceeds its bounded URL lookup entry count.");
        }
        captureWork.push({ requestedUrl, successful, aliases });
      }

      const assetsByHash = new Map();
      let uniquePayloadBytes = 0;
      for (const { requestedUrl, successful, aliases } of captureWork) {
        let chosenBytes = null;
        let chosenHash = "";
        let chosenMimeType = "";
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
            chosenMimeType = record.mimeType;
          } else if (chosenHash !== responseHash) {
            throw new Error(`Visible image key ${resourceUrlSha256(requestedUrl)} returned different bytes during one page capture.`);
          }
        }
        if (chosenBytes === null) {
          throw new Error(`Visible image key ${resourceUrlSha256(requestedUrl)} returned an empty response body.`);
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
      }
      return [...assetsByHash.values()]
        .map(asset => ({ ...asset, urlHashes: [...asset.urlHashes].sort() }))
        .sort((left, right) => left.sha256 < right.sha256 ? -1 : left.sha256 > right.sha256 ? 1 : 0);
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
  return { root, manifest, manifestSha256: sha256(manifestBytes), readmeBytes, assets, entriesByUrlHash };
}

export async function createSameByteReplayServer(corpusDirectory) {
  const corpus = await loadSameByteCorpus(corpusDirectory);
  let misses = 0;
  const server = http.createServer(async (request, response) => {
    const url = new URL(request.url || "/", "http://127.0.0.1");
    if (request.method !== "GET") {
      response.writeHead(405, { "content-type": "text/plain; charset=utf-8" }).end("Method Not Allowed");
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
        return;
      }
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
    get misses() { return misses; },
    close: () => closeServer(server),
  };
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
  if (manifest?.schemaVersion !== 1 || manifest.complete !== true ||
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
      manifest.limits?.maxReadmeBytes !== MAX_README_BYTES ||
      manifest.limits?.maxAssetBytes !== MAX_ASSET_BYTES ||
      manifest.limits?.maxAggregateAssetBytes !== MAX_CORPUS_ASSET_BYTES) {
    throw new Error("Same-byte corpus manifest is incomplete, unsupported, or malformed.");
  }
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
  return typeof value === "string" && value.length <= 128 && /^image\/[a-z0-9.+-]+$/iu.test(value);
}

function isGitSha1(value) {
  return typeof value === "string" && GIT_SHA1_PATTERN.test(value);
}

function closeServer(server) {
  return new Promise((resolve, reject) => {
    server.close(error => error ? reject(error) : resolve());
  });
}

async function readBoundedResponse(response, maximumBytes) {
  const contentLength = Number(response.headers.get("content-length"));
  if (Number.isSafeInteger(contentLength) && contentLength > maximumBytes) {
    throw new Error(`Pinned README response exceeds the ${maximumBytes}-byte safety limit.`);
  }
  let totalBytes = 0;
  const chunks = [];
  for await (const chunk of response.body) {
    const chunkSize = Number.isSafeInteger(chunk?.byteLength)
      ? chunk.byteLength
      : Buffer.byteLength(chunk);
    if (totalBytes + chunkSize > maximumBytes) {
      throw new Error(`Pinned README response exceeds the ${maximumBytes}-byte safety limit.`);
    }
    const bytes = Buffer.from(chunk);
    totalBytes += bytes.length;
    chunks.push(bytes);
  }
  return Buffer.concat(chunks, totalBytes);
}
