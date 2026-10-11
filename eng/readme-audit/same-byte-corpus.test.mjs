import assert from "node:assert/strict";
import http from "node:http";
import { mkdir, mkdtemp, readFile, rm, truncate, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import {
  captureSameByteCorpus,
  createCapturedImageAltIdentities,
  createResponseRecorder,
  createSameByteReplayServer,
  gitBlobSha1,
  PINNED_GFM_PARSER,
  PINNED_GITHUB_EMOJI_PARSER,
  PINNED_GITHUB_README_STYLE_PROFILE,
  PINNED_SOURCE_REPLAY_PARSER,
  readSameByteSnapshotFailureEvidence,
  resourceUrlSha256,
  sha256,
} from "./same-byte-corpus.mjs";
import { replaySourceBoundMarkdownInEdge } from "./same-byte-edge-source.mjs";

const imageUrl = "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/docs/image.png";
const readmeUrl = "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/README.md";
const readmeBytes = Buffer.from("![example](docs/image.png)\n", "utf8");
const imageBytes = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3]);
const renderedHtml = '<article class="markdown-body"><p>example</p><img alt="example" data-jithub-image-index="0"></article>';
const renderedHtmlTwoImages = '<article class="markdown-body"><img data-jithub-image-index="0"><img data-jithub-image-index="1"></article>';

test("captured image identity uses validated indexed rendered alts and hashes only", () => {
  const html = Buffer.from(
    '<article><img alt="A &amp; B" data-jithub-image-index="1"><img alt="first" data-jithub-image-index="0"></article>',
    "utf8");
  const routes = [{ index: 0 }, { index: 1 }];
  const identities = createCapturedImageAltIdentities(html, routes);
  assert.deepEqual(identities, [
    { index: 0, altSha256: sha256(Buffer.from("first", "utf8")) },
    { index: 1, altSha256: sha256(Buffer.from("A & B", "utf8")) },
  ]);
  assert.equal(JSON.stringify(identities).includes("first"), false);
  assert.equal(JSON.stringify(identities).includes("A & B"), false);

  assert.deepEqual(createCapturedImageAltIdentities(Buffer.from(
    '<article><img alt="" data-jithub-image-index="0"></article>', "utf8"), [{ index: 0 }]), [
    { index: 0, altSha256: null },
  ]);
  assert.deepEqual(createCapturedImageAltIdentities(Buffer.from(
    '<article><img alt="ambiguous &amp;alt;" data-jithub-image-index="0"></article>', "utf8"), [{ index: 0 }]), [
    { index: 0, altSha256: null },
  ]);
  assert.deepEqual(createCapturedImageAltIdentities(Buffer.from(
    '<article><img alt="one" data-jithub-image-index="0"><img alt="two" data-jithub-image-index="0"></article>', "utf8"), [{ index: 0 }]), []);
});

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

function emitImageResponse(cdp, url = imageUrl, encodedDataLength = imageBytes.length, mimeType = "image/png") {
  cdp.emit("Network.requestWillBeSent", {
    requestId: "image-1",
    request: { url },
  });
  cdp.emit("Network.responseReceived", {
    requestId: "image-1",
    type: "Image",
    response: { url, status: 200, mimeType },
  });
  cdp.emit("Network.loadingFinished", { requestId: "image-1", encodedDataLength });
}

async function createFixture(t, { image = imageBytes } = {}) {
  const directory = await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const assetHash = sha256(image);
  const manifest = {
    schemaVersion: 2,
    complete: true,
    repository: {
      ...repositoryFixture(),
      readmePath: "README.md",
      readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    },
    readme: { file: "readme.md", byteSize: readmeBytes.length, sha256: sha256(readmeBytes) },
    assets: [{ urlSha256: resourceUrlSha256(imageUrl), sha256: assetHash, byteSize: image.length, mimeType: "image/png" }],
    imageRoutes: [{ index: 0, urlSha256: resourceUrlSha256(imageUrl), sha256: assetHash, mimeType: "image/png" }],
    browserRender: { status: "passed", file: "rendered.html", byteSize: Buffer.byteLength(renderedHtml), sha256: sha256(Buffer.from(renderedHtml)) },
    limits: {
      maxReadmeBytes: 16 * 1024 * 1024,
      maxRenderedHtmlBytes: 32 * 1024 * 1024,
      maxAssetBytes: 64 * 1024 * 1024,
      maxAggregateAssetBytes: 256 * 1024 * 1024,
    },
  };
  await writeFile(path.join(directory, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);
  await writeFile(path.join(directory, "readme.md"), readmeBytes);
  await writeFile(path.join(directory, "rendered.html"), renderedHtml);
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
    renderedHtml: renderedHtmlTwoImages,
    responseRecorder: recorder,
    fetchImpl: async (url, options) => {
      assert.equal(url.href, readmeUrl);
      assert.ok(options.signal instanceof AbortSignal);
      return new Response(readmeBytes, { status: 200 });
    },
  });
  recorder.dispose();

  assert.equal(result.assetCount, 1);
  assert.equal(result.assetBytes, imageBytes.length);
  assert.equal(result.readmeBytes, readmeBytes.length);
  assert.equal(result.readmeSha256, sha256(readmeBytes));
  assert.equal(result.browserRender.status, "passed");
  const manifest = JSON.parse(await readFile(path.join(directory, "manifest.json"), "utf8"));
  assert.equal(manifest.repository.readmeGitBlobSha1, gitBlobSha1(readmeBytes));
  assert.equal(manifest.assets.length, 1);
  assert.ok(manifest.assets.every(asset => asset.sha256 === sha256(imageBytes)));
  assert.ok(manifest.assets.some(asset => asset.urlSha256 === resourceUrlSha256(imageUrl)));
  assert.equal(manifest.imageRoutes.length, 2);
  assert.equal(manifest.imageRoutes[0].index, 0);
  assert.equal(manifest.imageRoutes[1].index, 1);
  assert.equal(manifest.browserRender.sha256, sha256(Buffer.from(renderedHtmlTwoImages)));
  assert.equal(result.assetUrlMapSha256, sha256(Buffer.from(JSON.stringify(manifest.assets), "utf8")));
  assert.equal(JSON.stringify(manifest).includes("raw.githubusercontent.com"), false);
  assert.equal(JSON.stringify(manifest).includes("token"), false);
});

test("same-byte capture adds exact pinned Markdown source bytes when the browser alias is absent", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-source-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const sourceReadmeBytes = Buffer.from("![source-only](docs/source-only.png)\n", "utf8");
  const sourceImageBytes = Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP6zwAAAgcBApocMXEAAAAASUVORK5CYII=",
    "base64");
  const sourceImageUrl = "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/docs/source-only.png";
  const browserImageUrl = "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/docs/browser-capture.png";
  const requestedUrls = [];
  const result = await captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(sourceReadmeBytes),
    readmeByteSize: sourceReadmeBytes.length,
    images: [],
    renderedHtml: '<article class="markdown-body"><p>source only</p></article>',
    responseRecorder: {
      captureVisibleImages: async () => ({
        assets: [{
          sha256: sha256(sourceImageBytes),
          bytes: sourceImageBytes,
          mimeType: "image/png",
          urlHashes: [resourceUrlSha256(browserImageUrl)],
        }],
        imageRoutes: [],
      }),
    },
    fetchImpl: async url => {
      const requestUrl = new URL(url).href;
      requestedUrls.push(requestUrl);
      if (requestUrl === readmeUrl) return new Response(sourceReadmeBytes, { status: 200 });
      if (requestUrl === sourceImageUrl) return new Response(sourceImageBytes, { status: 200 });
      throw new Error(`Unexpected source request ${requestUrl}`);
    },
  });

  assert.deepEqual(requestedUrls, [readmeUrl, sourceImageUrl]);
  assert.deepEqual(result.sourceImageAliases, {
    reusedCount: 0,
    fetchedCount: 1,
    skippedCount: 0,
    byteMismatchCount: 0,
    sourceFetchAttemptCount: 1,
    sourceFetchBytes: sourceImageBytes.length,
    sourceFetchBudgetExhausted: false,
  });
  const manifestText = await readFile(path.join(directory, "manifest.json"), "utf8");
  const manifest = JSON.parse(manifestText);
  const sourceHash = sha256(sourceImageBytes);
  assert.equal(manifest.assets.length, 2);
  assert.ok(manifest.assets.every(asset => asset.sha256 === sourceHash));
  assert.ok(manifest.assets.some(asset => asset.urlSha256 === resourceUrlSha256(sourceImageUrl)));
  assert.equal(manifestText.includes("raw.githubusercontent.com"), false);
  assert.deepEqual(await readFile(path.join(directory, "assets", sourceHash)), sourceImageBytes);
});

test("same-byte capture extracts bounded HTML picture and image source aliases", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-html-source-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const sourceReadmeBytes = Buffer.from(
    '<picture><source type="image/gif" srcset="docs/selected.gif 1x, docs/alternate.gif 2x"><img data-canonical-src="docs/canonical.png?size=1&amp;format=png" src="docs/poster.jpg" alt="preview"></picture>\n',
    "utf8");
  const capturedBytes = imageBytes;
  const browserImageUrl = "https://raw.githubusercontent.com/example/repo/browser/selected.gif";
  const expectedUrls = [
    "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/docs/selected.gif",
    "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/docs/alternate.gif",
    "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/docs/canonical.png",
    "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/docs/poster.jpg",
  ];
  const requestedUrls = [];
  const result = await captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(sourceReadmeBytes),
    readmeByteSize: sourceReadmeBytes.length,
    images: [],
    renderedHtml: '<article class="markdown-body"><p>picture</p></article>',
    responseRecorder: {
      captureVisibleImages: async () => ({
        assets: [{
          sha256: sha256(capturedBytes),
          bytes: capturedBytes,
          mimeType: "image/png",
          urlHashes: [resourceUrlSha256(browserImageUrl)],
        }],
        imageRoutes: [],
      }),
    },
    fetchImpl: async url => {
      const requestUrl = new URL(url).href;
      requestedUrls.push(requestUrl);
      if (requestUrl === readmeUrl) return new Response(sourceReadmeBytes, { status: 200 });
      if (expectedUrls.slice(0, 3).includes(requestUrl)) {
        return new Response(capturedBytes, { status: 200, headers: { "content-type": "image/png" } });
      }
      if (requestUrl === expectedUrls[3]) {
        return new Response(Buffer.from("GIF89a-different-poster", "ascii"), { status: 200 });
      }
      throw new Error(`Unexpected source request ${requestUrl}`);
    },
  });

  assert.deepEqual(requestedUrls, [readmeUrl, ...expectedUrls]);
  assert.equal(result.sourceImageAliases.fetchedCount, 4);
  assert.equal(result.sourceImageAliases.byteMismatchCount, 1);
  assert.equal(result.sourceImageAliases.sourceFetchAttemptCount, 4);
  assert.equal(result.sourceImageAliases.sourceFetchBytes, capturedBytes.length * 3 + Buffer.byteLength("GIF89a-different-poster"));
  const manifestText = await readFile(path.join(directory, "manifest.json"), "utf8");
  const manifest = JSON.parse(manifestText);
  const sourceHash = sha256(capturedBytes);
  assert.ok(manifest.assets.some(asset => asset.urlSha256 === resourceUrlSha256(expectedUrls[0]) && asset.sha256 === sourceHash));
  assert.ok(manifest.assets.some(asset => asset.urlSha256 === resourceUrlSha256(expectedUrls[1]) && asset.sha256 === sourceHash));
  assert.ok(manifest.assets.some(asset => asset.urlSha256 === resourceUrlSha256(expectedUrls[2]) && asset.sha256 === sourceHash));
  assert.equal(manifest.assets.some(asset => asset.urlSha256 === resourceUrlSha256(expectedUrls[3])), false);
  assert.equal(manifest.assets.reduce((sum, asset) => sum + asset.byteSize, 0), capturedBytes.length * 4);
});

test("captured image MIME is canonicalized from bounded payload signatures", async () => {
  const fixtures = [
    [Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), "image/png"],
    [Buffer.from([0xff, 0xd8, 0xff, 0x00]), "image/jpeg"],
    [Buffer.from("GIF89a", "ascii"), "image/gif"],
    [Buffer.from("RIFF\x00\x00\x00\x00WEBP", "binary"), "image/webp"],
    [Buffer.from("BM\x00\x00", "binary"), "image/bmp"],
    [Buffer.from([0x00, 0x00, 0x01, 0x00]), "image/x-icon"],
    [Buffer.from([0, 0, 0, 16, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66, 0, 0, 0, 0]), "image/avif"],
    [Buffer.from('<?xml version="1.0"?><svg xmlns="http://www.w3.org/2000/svg"></svg>', "utf8"), "image/svg+xml"],
    [Buffer.from('<?xml version="1.0"?><!-- exported --><!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd"><svg xmlns="http://www.w3.org/2000/svg"></svg>', "utf8"), "image/svg+xml"],
  ];

  for (const [bytes, expectedMime] of fixtures) {
    const cdp = createFakeCdp(bytes);
    const recorder = createResponseRecorder(cdp);
    emitImageResponse(cdp, imageUrl, bytes.length, "application/octet-stream");
    const captured = await recorder.captureVisibleImages([imageFixture()]);
    recorder.dispose();
    assert.equal(captured.assets[0].mimeType, expectedMime);
    assert.equal(captured.imageRoutes[0].mimeType, expectedMime);
  }
});

test("captured image payload with an unknown signature fails closed", async () => {
  const bytes = Buffer.from("not an image", "utf8");
  const cdp = createFakeCdp(bytes);
  const recorder = createResponseRecorder(cdp);
  emitImageResponse(cdp, imageUrl, bytes.length, "application/octet-stream");
  await assert.rejects(
    recorder.captureVisibleImages([imageFixture()]),
    /unsupported or unrecognized image payload/u);
  recorder.dispose();
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
    renderedHtml,
    responseRecorder: recorder,
    fetchImpl: async () => new Response(readmeBytes, { status: 200 }),
  });
  recorder.dispose();

  const manifestText = await readFile(path.join(directory, "manifest.json"), "utf8");
  assert.equal(manifestText.includes("do-not-persist-this-value"), false);
  assert.equal(manifestText.includes("raw.githubusercontent.com"), false);
  assert.equal(manifestText.includes(resourceUrlSha256(signedUrl)), true);
});

test("GitHub Camo bytes replay under the authored canonical image URL", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const camoUrl = `https://camo.githubusercontent.com/example/${Buffer.from(imageUrl).toString("hex")}`;
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);
  emitImageResponse(cdp, camoUrl);

  await captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    readmeByteSize: readmeBytes.length,
    images: [{ ...imageFixture(camoUrl), canonicalSource: imageUrl }],
    renderedHtml,
    responseRecorder: recorder,
    fetchImpl: async () => new Response(readmeBytes, { status: 200 }),
  });
  recorder.dispose();

  const manifestText = await readFile(path.join(directory, "manifest.json"), "utf8");
  const manifest = JSON.parse(manifestText);
  assert.equal(manifest.assets.length, 2);
  assert.equal(manifest.assets.some(asset => asset.urlSha256 === resourceUrlSha256(imageUrl)), true);
  assert.equal(manifestText.includes(imageUrl), false);
  assert.equal(manifestText.includes(camoUrl), false);

  const replay = await createSameByteReplayServer(directory);
  t.after(replay.close);
  const response = await fetch(`${replay.baseUrl}/asset?url=${encodeURIComponent(imageUrl)}`);
  assert.equal(response.status, 200);
  assert.deepEqual(Buffer.from(await response.arrayBuffer()), imageBytes);
  assert.equal(replay.misses, 0);
});

test("distinct visible images are bounded before searching or reading responses", async () => {
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);
  const images = Array.from({ length: 15_001 }, (_, index) => imageFixture(`${imageUrl}?case=${index}`));
  await assert.rejects(
    recorder.captureVisibleImages(images),
    /bounded image-element count/u);
  recorder.dispose();
});

test("data and repeated image elements cannot bypass the total element cap", async () => {
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);
  const images = Array.from({ length: 15_001 }, (_, index) => imageFixture(
    index % 2 === 0 ? "data:image/png;base64,iVBORw0KGgo=" : imageUrl));
  await assert.rejects(
    recorder.captureVisibleImages(images),
    /bounded image-element count/u);
  recorder.dispose();
});

test("redirect alias storms are rejected before reading any image body", async () => {
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);
  for (let request = 0; request < 1_000; request++) {
    const requestId = `redirect-${request}`;
    cdp.emit("Network.requestWillBeSent", { requestId, request: { url: imageUrl } });
    for (let alias = 0; alias < 15; alias++) {
      cdp.emit("Network.requestWillBeSent", {
        requestId,
        request: { url: `${imageUrl}?redirect=${request}-${alias}` },
      });
    }
    cdp.emit("Network.responseReceived", {
      requestId,
      type: "Image",
      response: { url: `${imageUrl}?redirect=${request}-14`, status: 200, mimeType: "image/png" },
    });
    cdp.emit("Network.loadingFinished", { requestId, encodedDataLength: imageBytes.length });
  }
  await assert.rejects(
    recorder.captureVisibleImages([imageFixture()]),
    /bounded URL lookup entry count/u);
  recorder.dispose();
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
    readmeRendered: false,
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
  const renderedResponse = await fetch(`${replay.baseUrl}/rendered-html`);
  assert.equal(renderedResponse.headers.get("content-type"), "text/html; charset=utf-8");
  assert.equal(Number(renderedResponse.headers.get("content-length")), Buffer.byteLength(renderedHtml));
  assert.equal(renderedResponse.headers.get("x-content-sha256"), sha256(Buffer.from(renderedHtml)));
  assert.deepEqual(Buffer.from(await renderedResponse.arrayBuffer()), Buffer.from(renderedHtml));

  const signedImageUrl = `${imageUrl}?token=must-not-be-persisted`;
  const signedManifest = JSON.parse(await readFile(path.join(directory, "manifest.json"), "utf8"));
  signedManifest.assets[0].urlSha256 = resourceUrlSha256(signedImageUrl);
  signedManifest.imageRoutes[0].urlSha256 = resourceUrlSha256(signedImageUrl);
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

test("offline replay document is CSP-restricted and bound to the captured HTML digest", async t => {
  const directory = await createFixture(t);
  const replay = await createSameByteReplayServer(directory);
  t.after(replay.close);

  const response = await fetch(`${replay.baseUrl}/replay`);
  assert.equal(response.status, 200);
  assert.match(response.headers.get("content-security-policy") || "", /default-src 'none'/u);
  assert.equal(response.headers.get("x-content-sha256"), sha256(Buffer.from(renderedHtml)));
  assert.equal(replay.renderedHtmlSha256, sha256(Buffer.from(renderedHtml)));
  const imageResponse = await fetch(`${replay.baseUrl}/asset-by-content-sha256/${sha256(imageBytes)}?index=0`);
  assert.equal(imageResponse.headers.get("x-content-sha256"), sha256(imageBytes));
  assert.deepEqual(Buffer.from(await imageResponse.arrayBuffer()), imageBytes);
  assert.deepEqual(replay.servedUrlSha256s, [resourceUrlSha256(imageUrl)]);
  assert.equal(replay.servedImageRoutes, 1);
});

test("source-bound Edge page serves exact README and SRI-pinned Marked bytes", async t => {
  const directory = await createFixture(t);
  const replay = await createSameByteReplayServer(directory);
  t.after(replay.close);

  const sourcePage = await fetch(`${replay.baseUrl}/source`);
  const sourceHtml = await sourcePage.text();
  assert.equal(sourcePage.status, 200);
  assert.match(sourcePage.headers.get("content-security-policy") || "", /default-src 'none'; script-src 'self'/u);
  assert.match(sourcePage.headers.get("content-security-policy") || "", /style-src 'self' 'unsafe-inline'/u);
  const styleIntegrity = `sha256-${Buffer.from(PINNED_GITHUB_README_STYLE_PROFILE.sha256, "hex").toString("base64")}`;
  assert.ok(sourceHtml.includes(
    `<link rel="stylesheet" href="${PINNED_GITHUB_README_STYLE_PROFILE.cssPath}" ` +
    `data-markdown-style-profile="${PINNED_GITHUB_README_STYLE_PROFILE.id}" ` +
    `data-markdown-style-profile-version="${PINNED_GITHUB_README_STYLE_PROFILE.version}" ` +
    `integrity="${styleIntegrity}" crossorigin="anonymous">`));
  assert.doesNotMatch(sourceHtml, /<style(?:\s|>)/iu,
    "the source oracle must consume the canonical stylesheet rather than duplicate declarations inline");
  assert.match(sourceHtml, /src="\/marked-parser\.js" integrity="sha256-[A-Za-z0-9+/]+=*" crossorigin="anonymous"/u);
  assert.match(sourceHtml, /src="\/github-emoji-shortcodes\.js" integrity="sha256-[A-Za-z0-9+/]+=*" crossorigin="anonymous"/u);

  const styleResponse = await fetch(`${replay.baseUrl}${PINNED_GITHUB_README_STYLE_PROFILE.cssPath}`);
  const styleBytes = Buffer.from(await styleResponse.arrayBuffer());
  const styleText = styleBytes.toString("utf8");
  assert.equal(styleResponse.status, 200);
  assert.equal(styleResponse.headers.get("content-type"), "text/css; charset=utf-8");
  assert.equal(styleResponse.headers.get("x-markdown-style-profile-id"), PINNED_GITHUB_README_STYLE_PROFILE.id);
  assert.equal(styleResponse.headers.get("x-markdown-style-profile-version"),
    String(PINNED_GITHUB_README_STYLE_PROFILE.version));
  assert.equal(styleResponse.headers.get("x-content-sha256"), PINNED_GITHUB_README_STYLE_PROFILE.sha256);
  assert.equal(sha256(styleBytes), PINNED_GITHUB_README_STYLE_PROFILE.sha256,
    "Edge profile CSS bytes must match the same pinned fingerprint exposed to the native profile");
  assert.match(styleText, /font-size:\s*16px;\s*line-height:\s*1\.5;\s*overflow-wrap:\s*break-word/u);
  assert.match(styleText, /html\s*\{\s*scrollbar-width:\s*none;\s*\}/u,
    "the source replay must use the full CSS content width like WinUI's overlay scrollbar");
  assert.match(styleText, /html::-webkit-scrollbar\s*\{\s*width:\s*0;\s*height:\s*0;\s*\}/u,
    "the source replay also hides classic Chromium scrollbars while retaining page scrolling");
  assert.doesNotMatch(styleText, /overflow(?:-y)?:\s*hidden/u,
    "viewport parity must not be achieved by disabling document scrolling");
  assert.match(styleText, /img\s*\{\s*max-width:\s*100%;\s*vertical-align:\s*middle;/u);
  assert.doesNotMatch(styleText, /height:\s*auto/u,
    "source profile must preserve authored HTML image height hints");
  assert.match(styleText, /li\s*\+\s*li\s*\{\s*margin-top:\s*\.25em;/u,
    "tight-list sibling spacing must remain in the pinned source oracle");
  assert.match(styleText, /tr:nth-child\s*\(\s*2n\s*\)/u,
    "the source oracle must retain the captured article's table striping");
  assert.match(styleText, /h1,\s*\.markdown-body h2\s*\{\s*padding-bottom:\s*\.3em;\s*border-bottom:/u,
    "the source oracle must retain the captured article's heading rules");
  assert.equal(replay.styleProfile.id, PINNED_GITHUB_README_STYLE_PROFILE.id);
  assert.equal(replay.styleProfile.version, PINNED_GITHUB_README_STYLE_PROFILE.version);
  assert.equal(replay.styleProfile.sha256, PINNED_GITHUB_README_STYLE_PROFILE.sha256);
  assert.equal(replay.servedStyleProfileCount, 1);

  const parserResponse = await fetch(`${replay.baseUrl}/marked-parser.js`);
  const parserBytes = Buffer.from(await parserResponse.arrayBuffer());
  assert.equal(parserResponse.headers.get("x-content-sha256"), PINNED_GFM_PARSER.sha256);
  assert.equal(sha256(parserBytes), PINNED_GFM_PARSER.sha256);
  const repeatedParserResponse = await fetch(`${replay.baseUrl}/marked-parser.js`);
  assert.equal(repeatedParserResponse.headers.get("x-content-sha256"), PINNED_GFM_PARSER.sha256);
  assert.deepEqual(Buffer.from(await repeatedParserResponse.arrayBuffer()), parserBytes);
  assert.equal(replay.servedParserCount, 2);
  const emojiExtensionResponse = await fetch(`${replay.baseUrl}/github-emoji-shortcodes.js`);
  const emojiExtensionBytes = Buffer.from(await emojiExtensionResponse.arrayBuffer());
  assert.equal(emojiExtensionResponse.headers.get("x-content-sha256"), PINNED_GITHUB_EMOJI_PARSER.extensionSha256);
  assert.equal(sha256(emojiExtensionBytes), PINNED_GITHUB_EMOJI_PARSER.extensionSha256);
  const emojiMapResponse = await fetch(`${replay.baseUrl}/github-emoji-shortcodes.json`);
  const emojiMapBytes = Buffer.from(await emojiMapResponse.arrayBuffer());
  assert.equal(emojiMapResponse.headers.get("x-content-sha256"), PINNED_GITHUB_EMOJI_PARSER.mapSha256);
  assert.equal(sha256(emojiMapBytes), PINNED_GITHUB_EMOJI_PARSER.mapSha256);
  assert.equal(Object.keys(JSON.parse(emojiMapBytes.toString("utf8"))).length, PINNED_GITHUB_EMOJI_PARSER.aliasCount);
  assert.equal(replay.servedEmojiExtensionCount, 1);
  assert.equal(replay.servedEmojiMapCount, 1);
  assert.equal(replay.parser.version, "18.0.5");
  assert.equal(replay.parser.license, "MIT");
  assert.deepEqual(replay.parser, PINNED_SOURCE_REPLAY_PARSER);

  const manifestResponse = await fetch(`${replay.baseUrl}/manifest`);
  assert.equal(manifestResponse.headers.get("x-content-sha256"), replay.manifestSha256);
  assert.deepEqual(Buffer.from(await manifestResponse.arrayBuffer()), replay.manifestBytes);
  const readmeResponse = await fetch(`${replay.baseUrl}/readme`);
  assert.equal(readmeResponse.headers.get("x-content-sha256"), sha256(readmeBytes));
  assert.deepEqual(Buffer.from(await readmeResponse.arrayBuffer()), readmeBytes);

  const imageResponse = await fetch(`${replay.baseUrl}/asset-by-url-sha256/${resourceUrlSha256(imageUrl)}`);
  assert.equal(imageResponse.headers.get("x-content-sha256"), sha256(imageBytes));
  assert.deepEqual(Buffer.from(await imageResponse.arrayBuffer()), imageBytes);
});

test("source replay binds Edge's content box and semantic article width to the native viewport", async () => {
  const sourceReplaySource = await readFile(
    new URL("./same-byte-edge-source.mjs", import.meta.url), "utf8");

  assert.match(sourceReplaySource, /documentClientWidth:\s*document\.documentElement\.clientWidth/u);
  assert.match(sourceReplaySource, /bodyClientWidth:\s*document\.body\.clientWidth/u);
  assert.match(sourceReplaySource, /articleWidth:\s*article\.getBoundingClientRect\(\)\.width/u);
  assert.match(sourceReplaySource,
    /viewportWidthEvidence\.documentClientWidth\s*!==\s*innerWidth[\s\S]*?viewportWidthEvidence\.bodyClientWidth\s*!==\s*innerWidth[\s\S]*?Math\.abs\(viewportWidthEvidence\.articleWidth\s*-\s*innerWidth\)\s*>\s*0\.5/u,
    "matching outer viewport dimensions alone must not admit a narrowed Edge article");
  assert.match(sourceReplaySource,
    /documentClientWidth:\s*replay\.viewport\.documentClientWidth,[\s\S]*?bodyClientWidth:\s*replay\.viewport\.bodyClientWidth,[\s\S]*?articleWidth:\s*replay\.viewport\.articleWidth/u,
    "the replay report must retain all effective content-width evidence for native gating");
});

test("source-view capture is explicitly not applicable to browser HTML replay", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const recorder = createResponseRecorder(createFakeCdp());
  const result = await captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    readmeByteSize: readmeBytes.length,
    images: [],
    readmeRendered: false,
    responseRecorder: recorder,
    fetchImpl: async () => new Response(readmeBytes, { status: 200 }),
  });
  recorder.dispose();
  const manifest = JSON.parse(await readFile(path.join(directory, "manifest.json"), "utf8"));
  assert.deepEqual(manifest.browserRender, { status: "not-applicable", reason: "github-source-view" });
  assert.equal(result.browserRender.status, "not-applicable");
  assert.equal(manifest.imageRoutes.length, 0);

  const replay = await createSameByteReplayServer(directory);
  t.after(replay.close);
  await assert.rejects(replaySourceBoundMarkdownInEdge({
    cdp: {},
    replayServer: replay,
    outputDirectory: directory,
    viewport: { width: 640, height: 480, deviceScaleFactor: 1 },
    semanticDigestKey: "ab".repeat(32),
  }), /requires a captured GitHub-rendered article/u);
});

test("data images are bound to the indexed captured HTML without exposing a data URL in the manifest", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const dataUri = `data:image/svg+xml;base64,${Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"/>').toString("base64")}`;
  const recorder = createResponseRecorder(createFakeCdp());
  const result = await captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    readmeByteSize: readmeBytes.length,
    images: [imageFixture(dataUri)],
    renderedHtml: `<article><img data-jithub-image-index="0" data-jithub-image-data="${dataUri}"></article>`,
    responseRecorder: recorder,
    fetchImpl: async () => new Response(readmeBytes, { status: 200 }),
  });
  recorder.dispose();

  const manifestText = await readFile(path.join(directory, "manifest.json"), "utf8");
  const manifest = JSON.parse(manifestText);
  assert.equal(result.assetCount, 0);
  assert.deepEqual(manifest.imageRoutes, [{ index: 0, dataUri: true }]);
  assert.equal(manifestText.includes("data:image"), false);
  const replay = await createSameByteReplayServer(directory);
  t.after(replay.close);
  assert.equal(replay.expectedVisibleImageCount, 1);
});

test("rendered snapshot rejects each active-content predicate with privacy-safe diagnostics", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);
  emitImageResponse(cdp);
  const common = {
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    readmeByteSize: readmeBytes.length,
    images: [imageFixture()],
    responseRecorder: recorder,
    fetchImpl: async () => new Response(readmeBytes, { status: 200 }),
  };
  await assert.rejects(captureSameByteCorpus({
    ...common,
    renderedHtml: '<article><img src="https://example.org/image.png" data-jithub-image-index="0"></article>',
  }), error => {
    assert.deepEqual(readSameByteSnapshotFailureEvidence(error), {
      failureStage: "rendered-snapshot-validation",
      failurePredicate: "external-resource-attribute",
      elementTag: "img",
      attributeName: "src",
    });
    return true;
  });
  await assert.rejects(captureSameByteCorpus({
    ...common,
    renderedHtml: '<article><img data-jithub-image-index="1"></article>',
  }), error => {
    assert.deepEqual(readSameByteSnapshotFailureEvidence(error), {
      failureStage: "rendered-snapshot-validation",
      failurePredicate: "missing-image-index",
    });
    return true;
  });
  recorder.dispose();

  const diagnosticCases = [
    ["invalid-root", "not-an-article", []],
    ["active-element-tag", "<article><script>blocked()</script></article>", []],
    ["stylesheet-link", '<article><link rel="stylesheet" href="https://private.example.invalid/style.css"></article>', []],
    ["event-handler-attribute", '<article><img onerror="blocked()"></article>', []],
    ["external-resource-attribute", '<article><img src="https://private.example.invalid/image.png"></article>', []],
    ["style-element", "<article><style>body{color:red}</style></article>", []],
    ["external-css-resource", '<article><div style="background-image:url(https://private.example.invalid/image.png)"></div></article>', []],
    ["duplicate-image-index", '<article><img data-jithub-image-index="0"><img data-jithub-image-index="0"></article>', []],
    ["missing-image-index", "<article><p>missing image route</p></article>", [{ index: 0, dataUri: false }]],
    ["missing-data-image", '<article><img data-jithub-image-index="0"></article>', [{ index: 0, dataUri: true }]],
    ["network-image-data-payload", '<article><img data-jithub-image-index="0" data-jithub-image-data="data:image/png;base64,AA=="></article>', [{ index: 0, dataUri: false }]],
  ];
  for (const [predicate, html, imageRoutes] of diagnosticCases) {
    const diagnosticDirectory = path.join(directory, predicate);
    const diagnosticRecorder = {
      captureVisibleImages: async () => ({ assets: [], imageRoutes }),
    };
    await assert.rejects(captureSameByteCorpus({
      ...common,
      directory: diagnosticDirectory,
      images: [],
      renderedHtml: html,
      responseRecorder: diagnosticRecorder,
    }), error => {
      const evidence = readSameByteSnapshotFailureEvidence(error);
      const expectedEvidence = {
        failureStage: "rendered-snapshot-validation",
        failurePredicate: predicate,
      };
      const safeDetails = {
        "active-element-tag": { elementTag: "script" },
        "stylesheet-link": { elementTag: "link", attributeName: "rel" },
        "event-handler-attribute": { elementTag: "img" },
        "external-resource-attribute": { elementTag: "img", attributeName: "src" },
        "style-element": { elementTag: "style" },
        "external-css-resource": { elementTag: "div", attributeName: "style" },
        "duplicate-image-index": { elementTag: "img", attributeName: "data-jithub-image-index" },
      }[predicate];
      assert.deepEqual(evidence, { ...expectedEvidence, ...(safeDetails || {}) });
      const serialized = JSON.stringify(evidence);
      assert.doesNotMatch(serialized, /https?:\/\/|private\.example|data:image|blocked\(\)|<article/u);
      return true;
    });
  }
  const escapedTextDirectory = path.join(directory, "escaped-markup-text");
  await captureSameByteCorpus({
    ...common,
    directory: escapedTextDirectory,
    images: [],
    renderedHtml: '<article><pre>README example: &lt;img src=&quot;https://private.example.invalid/image.png&quot;&gt;</pre></article>',
    responseRecorder: { captureVisibleImages: async () => ({ assets: [], imageRoutes: [] }) },
  });
  const textOnlyManifest = JSON.parse(await readFile(path.join(escapedTextDirectory, "manifest.json"), "utf8"));
  assert.equal(textOnlyManifest.imageRoutes.length, 0,
    "escaped code/text mentioning a resource attribute is not treated as a live element attribute");
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
    renderedHtml,
    responseRecorder: recorder,
    githubToken: "",
    fetchImpl: async () => new Response(readmeBytes, { status: 200 }),
  }), /do not match the pinned GitHub blob identity/u);
  recorder.dispose();
});

test("pinned README symlink blobs resolve through the authenticated Contents API without weakening blob checks", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const symlinkBytes = Buffer.from("packages/next/README.md", "utf8");
  const resolvedBytes = Buffer.from("# Next.js\nPinned resolved README bytes.\n", "utf8");
  const resolvedSha = gitBlobSha1(resolvedBytes);
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);
  const observed = [];

  const result = await captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: resolvedSha,
    readmeByteSize: resolvedBytes.length,
    images: [],
    readmeRendered: false,
    responseRecorder: recorder,
    githubToken: "read-only-audit-test-token",
    fetchImpl: async (url, options) => {
      observed.push({ url: new URL(url), options });
      if (new URL(url).href === readmeUrl) {
        return new Response(symlinkBytes, { status: 200 });
      }

      assert.equal(new URL(url).href,
        "https://api.github.com/repos/example/repo/readme?ref=0123456789012345678901234567890123456789");
      return Response.json({
        name: "README.md",
        path: "packages/next/README.md",
        sha: resolvedSha,
        size: resolvedBytes.length,
        type: "file",
        encoding: "base64",
        content: `${resolvedBytes.toString("base64")}\n`,
      });
    },
  });
  recorder.dispose();

  assert.equal(observed.length, 2);
  assert.equal(observed[1].options.headers.authorization, "Bearer read-only-audit-test-token");
  assert.equal(observed[1].options.headers.accept, "application/vnd.github+json");
  assert.equal(observed[1].options.redirect, "error");
  assert.ok(observed[0].options.signal instanceof AbortSignal);
  assert.ok(observed[1].options.signal instanceof AbortSignal);
  assert.equal(result.readmeBytes, resolvedBytes.length);
  assert.equal(result.readmeSha256, sha256(resolvedBytes));
  assert.deepEqual(await readFile(path.join(directory, "readme.md")), resolvedBytes);
  const manifestText = await readFile(path.join(directory, "manifest.json"), "utf8");
  const manifest = JSON.parse(manifestText);
  assert.equal(manifest.readme.sha256, sha256(resolvedBytes));
  assert.equal(manifest.repository.readmeGitBlobSha1, resolvedSha);
  assert.equal(manifestText.includes("read-only-audit-test-token"), false);
});

test("a throttled raw README uses one authenticated, blob-verified Contents fallback", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const bytes = Buffer.from("# Rate-limited raw endpoint\n", "utf8");
  const blobSha = gitBlobSha1(bytes);
  const recorder = createResponseRecorder(createFakeCdp());
  t.after(() => recorder.dispose());
  const requests = [];

  const result = await captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: blobSha,
    readmeByteSize: bytes.length,
    images: [],
    readmeRendered: false,
    responseRecorder: recorder,
    githubToken: "read-only-audit-test-token",
    fetchImpl: async (url, options) => {
      requests.push(new URL(url).hostname);
      if (new URL(url).href === readmeUrl) return new Response(null, { status: 429 });
      assert.equal(options.headers.authorization, "Bearer read-only-audit-test-token");
      return Response.json({
        name: "README.md", path: "README.md", sha: blobSha, size: bytes.length,
        type: "file", encoding: "base64", content: bytes.toString("base64"),
      });
    },
  });

  assert.deepEqual(requests, ["raw.githubusercontent.com", "api.github.com"]);
  assert.equal(result.readmeSha256, sha256(bytes));
  assert.deepEqual(await readFile(path.join(directory, "readme.md")), bytes);
  assert.equal((await readFile(path.join(directory, "manifest.json"), "utf8"))
    .includes("read-only-audit-test-token"), false);
});

test("a throttled raw README without an audit token fails before another request", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const recorder = createResponseRecorder(createFakeCdp());
  t.after(() => recorder.dispose());
  let requests = 0;
  await assert.rejects(captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    readmeByteSize: readmeBytes.length,
    images: [],
    readmeRendered: false,
    responseRecorder: recorder,
    githubToken: "",
    fetchImpl: async () => {
      requests++;
      return new Response(null, { status: 429 });
    },
  }), /HTTP 429; authenticated pinned fallback is unavailable/u);
  assert.equal(requests, 1);
});

test("resolved README fallback fails closed when Contents API metadata disagrees with the pin", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const symlinkBytes = Buffer.from("packages/next/README.md", "utf8");
  const resolvedBytes = Buffer.from("Pinned content\n", "utf8");
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);

  await assert.rejects(captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(resolvedBytes),
    readmeByteSize: resolvedBytes.length,
    images: [],
    readmeRendered: false,
    responseRecorder: recorder,
    githubToken: "read-only-audit-test-token",
    fetchImpl: async url => new URL(url).hostname === "raw.githubusercontent.com"
      ? new Response(symlinkBytes, { status: 200 })
      : Response.json({
        path: "packages/next/README.md",
        sha: "f".repeat(40),
        size: resolvedBytes.length,
        type: "file",
        encoding: "base64",
        content: resolvedBytes.toString("base64"),
      }),
  }), /metadata does not match the expected README blob/u);
  recorder.dispose();
});

test("resolved README fallback bounds the Contents API response before JSON parsing", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-output-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const symlinkBytes = Buffer.from("packages/next/README.md", "utf8");
  const resolvedBytes = Buffer.from("Pinned content\n", "utf8");
  const cdp = createFakeCdp();
  const recorder = createResponseRecorder(cdp);

  await assert.rejects(captureSameByteCorpus({
    directory,
    repository: repositoryFixture(),
    readmeUrl,
    readmePath: "README.md",
    readmeGitBlobSha1: gitBlobSha1(resolvedBytes),
    readmeByteSize: resolvedBytes.length,
    images: [],
    readmeRendered: false,
    responseRecorder: recorder,
    githubToken: "read-only-audit-test-token",
    fetchImpl: async url => new URL(url).hostname === "raw.githubusercontent.com"
      ? new Response(symlinkBytes, { status: 200 })
      : new Response("{}", {
        status: 200,
        headers: { "content-length": String(25 * 1024 * 1024) },
      }),
  }), /GitHub README API response exceeds the 25165824-byte safety limit/u);
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
    renderedHtml,
    responseRecorder: recorder,
    fetchImpl: async () => new Response(new ReadableStream({
      start(controller) { controller.enqueue(oversizedChunk); controller.close(); },
    }), { status: 200 }),
  }), /Pinned README response exceeds/u);
  recorder.dispose();
});

test("offline replay server closes an active Edge-style response promptly", async t => {
  const largeAsset = Buffer.alloc(32 * 1024 * 1024, 0x5a);
  const directory = await createFixture(t, { image: largeAsset });
  const replay = await createSameByteReplayServer(directory);
  const assetHash = sha256(largeAsset);
  const request = http.get(`${replay.baseUrl}/asset-by-content-sha256/${assetHash}?index=0`);
  request.on("error", () => {});
  const response = await new Promise((resolve, reject) => {
    request.once("response", resolve);
    request.once("error", reject);
  });
  response.on("error", () => {});
  response.pause();

  let timeout;
  try {
    await Promise.race([
      replay.close(),
      new Promise((_, reject) => {
        timeout = setTimeout(() => reject(new Error("Replay server close blocked on an active response.")), 1000);
      }),
    ]);
  } finally {
    clearTimeout(timeout);
    request.destroy();
  }
});
