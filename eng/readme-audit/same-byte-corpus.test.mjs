import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, truncate, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import {
  captureSameByteCorpus,
  createResponseRecorder,
  createSameByteReplayServer,
  gitBlobSha1,
  resourceUrlSha256,
  sha256,
} from "./same-byte-corpus.mjs";

const imageUrl = "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/docs/image.png";
const readmeUrl = "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/README.md";
const readmeBytes = Buffer.from("![example](docs/image.png)\n", "utf8");
const imageBytes = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3]);

function repositoryFixture() {
  return {
    fullName: "example/repo",
    commitSha: "0123456789012345678901234567890123456789",
  };
}

function imageFixture(source = imageUrl) {
  return {
    source,
    currentSource: source,
    complete: true,
    naturalWidth: 1,
    naturalHeight: 1,
  };
}

function createFakeCdp(responseBytes = imageBytes) {
  const listeners = new Map();
  return {
    on(method, listener) {
      const handlers = listeners.get(method) ?? new Set();
      handlers.add(listener);
      listeners.set(method, handlers);
      return () => handlers.delete(listener);
    },
    emit(method, params) {
      for (const listener of listeners.get(method) ?? []) listener(params);
    },
    async send(method, params) {
      assert.equal(method, "Network.getResponseBody");
      assert.equal(params.requestId, "image-1");
      return { body: responseBytes.toString("base64"), base64Encoded: true };
    },
  };
}

function emitImageResponse(cdp, url = imageUrl, encodedDataLength = imageBytes.length) {
  cdp.emit("Network.requestWillBeSent", {
    requestId: "image-1",
    request: { url },
  });
  cdp.emit("Network.responseReceived", {
    requestId: "image-1",
    type: "Image",
    response: { url, status: 200, mimeType: "image/png" },
  });
  cdp.emit("Network.loadingFinished", { requestId: "image-1", encodedDataLength });
}

async function createFixture(t, { image = imageBytes } = {}) {
  const directory = await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const assetHash = sha256(image);
  const manifest = {
    schemaVersion: 1,
    complete: true,
    repository: {
      ...repositoryFixture(),
      readmePath: "README.md",
      readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    },
    readme: { file: "readme.md", byteSize: readmeBytes.length, sha256: sha256(readmeBytes) },
    assets: [{ urlSha256: resourceUrlSha256(imageUrl), sha256: assetHash, byteSize: image.length, mimeType: "image/png" }],
    limits: {
      maxReadmeBytes: 16 * 1024 * 1024,
      maxAssetBytes: 64 * 1024 * 1024,
      maxAggregateAssetBytes: 256 * 1024 * 1024,
    },
  };
  await writeFile(path.join(directory, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);
  await writeFile(path.join(directory, "readme.md"), readmeBytes);
  await mkdir(path.join(directory, "assets"));
  await writeFile(path.join(directory, "assets", assetHash), image);
  return directory;
}

test("same-byte capture pins raw README blob and content-addresses decoded Edge image bytes", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);
  emitImageResponse(cdp);
  const result = await captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    readmeByteSize: readmeBytes.length,
    images: [imageFixture(), imageFixture()],
    responseRecorder: recorder,
    fetchImpl: async url => {
      assert.equal(url.href, readmeUrl);
      return new Response(readmeBytes, { status: 200 });
    },
  });
  recorder.dispose();

  assert.equal(result.assetCount, 1);
  assert.equal(result.assetBytes, imageBytes.length);
  assert.equal(result.readmeBytes, readmeBytes.length);
  const manifest = JSON.parse(await readFile(path.join(directory, "manifest.json"), "utf8"));
  assert.equal(manifest.repository.readmeGitBlobSha1, gitBlobSha1(readmeBytes));
  assert.equal(manifest.assets.length, 1);
  assert.equal(manifest.assets[0].sha256, sha256(imageBytes));
  assert.equal(manifest.assets[0].urlSha256, resourceUrlSha256(imageUrl));
  assert.equal(JSON.stringify(manifest).includes("raw.githubusercontent.com"), false);
  assert.equal(JSON.stringify(manifest).includes("token"), false);
});

test("signed source queries are represented only by URL hashes in the captured corpus", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const signedUrl = `${imageUrl}?signature=do-not-persist-this-value`;
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);
  emitImageResponse(cdp, signedUrl);

  await captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    readmeByteSize: readmeBytes.length,
    images: [imageFixture(signedUrl)],
    responseRecorder: recorder,
    fetchImpl: async () => new Response(readmeBytes, { status: 200 }),
  });
  recorder.dispose();

  const manifestText = await readFile(path.join(directory, "manifest.json"), "utf8");
  assert.equal(manifestText.includes("do-not-persist-this-value"), false);
  assert.equal(manifestText.includes("raw.githubusercontent.com"), false);
  assert.equal(manifestText.includes(resourceUrlSha256(signedUrl)), true);
});

test("a source-preview README can capture exact Markdown bytes with no Edge image assets", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);

  const result = await captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    readmeByteSize: readmeBytes.length,
    images: [],
    responseRecorder: recorder,
    fetchImpl: async () => new Response(readmeBytes, { status: 200 }),
  });

  assert.equal(result.assetCount, 0);
  assert.equal(result.assetBytes, 0);
  const replay = await createSameByteReplayServer(directory);
  t.after(replay.close);
  assert.equal((await fetch(`${replay.baseUrl}/readme`)).headers.get("x-content-sha256"), sha256(readmeBytes));
  recorder.dispose();
});

test("same-byte replay serves pinned bytes and reports uncaptured URLs as a closed miss", async t => {
  const directory = await createFixture(t);
  const replay = await createSameByteReplayServer(directory);
  t.after(replay.close);

  const readmeResponse = await fetch(`${replay.baseUrl}/readme`);
  assert.equal(readmeResponse.headers.get("x-content-sha256"), sha256(readmeBytes));
  assert.deepEqual(Buffer.from(await readmeResponse.arrayBuffer()), readmeBytes);

  const signedImageUrl = `${imageUrl}?token=must-not-be-persisted`;
  const signedManifest = JSON.parse(await readFile(path.join(directory, "manifest.json"), "utf8"));
  signedManifest.assets[0].urlSha256 = resourceUrlSha256(signedImageUrl);
  await writeFile(path.join(directory, "manifest.json"), `${JSON.stringify(signedManifest, null, 2)}\n`);
  const signedReplay = await createSameByteReplayServer(directory);
  t.after(signedReplay.close);
  const assetResponse = await fetch(`${signedReplay.baseUrl}/asset?url=${encodeURIComponent(signedImageUrl)}`);
  assert.equal(assetResponse.headers.get("x-content-sha256"), sha256(imageBytes));
  assert.deepEqual(Buffer.from(await assetResponse.arrayBuffer()), imageBytes);

  const miss = await fetch(`${replay.baseUrl}/asset?url=${encodeURIComponent("https://example.org/not-captured.png")}`);
  assert.equal(miss.status, 404);
  assert.equal(miss.headers.get("x-same-byte-replay"), "miss");
  assert.equal(replay.misses, 1);
});

test("same-byte replay rejects a missing or mutated asset before serving", async t => {
  const directory = await createFixture(t);
  const manifest = JSON.parse(await readFile(path.join(directory, "manifest.json"), "utf8"));
  await writeFile(path.join(directory, "assets", manifest.assets[0].sha256), Buffer.from("tampered"));

  await assert.rejects(createSameByteReplayServer(directory), /does not match its SHA-256/u);
});

test("same-byte replay rejects oversized files and path-like content addresses", async t => {
  const directory = await createFixture(t);
  const manifestPath = path.join(directory, "manifest.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  const assetPath = path.join(directory, "assets", manifest.assets[0].sha256);
  await truncate(assetPath, 64 * 1024 * 1024 + 1);
  await assert.rejects(createSameByteReplayServer(directory), /exceeds its byte limit/u);

  await writeFile(assetPath, imageBytes);
  manifest.assets[0].sha256 = "../readme.md";
  await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
  await assert.rejects(createSameByteReplayServer(directory), /invalid image entry/u);
});

test("same-byte capture rejects a README whose bytes disagree with the immutable blob SHA", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);
  emitImageResponse(cdp);

  await assert.rejects(captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: "f".repeat(40),
    readmeByteSize: readmeBytes.length,
    images: [imageFixture()],
    responseRecorder: recorder,
    fetchImpl: async () => new Response(readmeBytes, { status: 200 }),
  }), /do not match the pinned GitHub blob identity/u);
  recorder.dispose();
});

test("CDP capture bounds actual decoded image bytes even when transfer length under-reports", async () => {
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp, { maxAssetBytes: imageBytes.length - 1 });
  emitImageResponse(cdp, imageUrl, 1);

  await assert.rejects(
    recorder.captureVisibleImages([imageFixture()]),
    /exceeds the per-image byte limit/u);
  recorder.dispose();
});

test("README capture enforces its streaming cap before accumulating the full body", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);
  const oversizedChunk = Buffer.alloc(16 * 1024 * 1024 + 1, 1);

  await assert.rejects(captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    readmeByteSize: readmeBytes.length,
    images: [],
    responseRecorder: recorder,
    fetchImpl: async () => new Response(new ReadableStream({
      start(controller) { controller.enqueue(oversizedChunk); controller.close(); },
    }), { status: 200 }),
  }), /Pinned README response exceeds/u);
  recorder.dispose();
});
