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
  assert.equal(manifest.assets[0].sha256, sha256(imageBytes));
  assert.equal(manifest.assets[0].urlSha256, resourceUrlSha256(imageUrl));
  assert.equal(manifest.imageRoutes.length, 2);
  assert.equal(manifest.imageRoutes[0].index, 0);
  assert.equal(manifest.imageRoutes[1].index, 1);
  assert.equal(manifest.browserRender.sha256, sha256(Buffer.from(renderedHtmlTwoImages)));
  assert.equal(result.assetUrlMapSha256, sha256(Buffer.from(JSON.stringify(manifest.assets), "utf8")));
  assert.equal(JSON.stringify(manifest).includes("raw.githubusercontent.com"), false);
  assert.equal(JSON.stringify(manifest).includes("token"), false);
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
  assert.match(sourceHtml, /src="\/marked-parser\.js" integrity="sha256-[A-Za-z0-9+/]+=*" crossorigin="anonymous"/u);
  assert.match(sourceHtml, /\.markdown-body\{[^}]*font-size:16px;line-height:1\.5;overflow-wrap:break-word/u);
  assert.match(sourceHtml, /\.markdown-body img\{max-width:100%;vertical-align:middle\}/u);
  assert.doesNotMatch(sourceHtml, /\.markdown-body img\{[^}]*height:auto/u,
    "source replay must not override authored HTML image height hints");
  assert.match(sourceHtml, /\.markdown-body li\+li\{margin-top:\.25em\}/u);
  assert.match(sourceHtml, /\.markdown-body pre\{[^}]*padding:16px;[^}]*font-size:85%;line-height:1\.45\}/u);
  assert.match(sourceHtml, /\.markdown-body pre code\{[^}]*font-size:100%\}/u);

  const parserResponse = await fetch(`${replay.baseUrl}/marked-parser.js`);
  const parserBytes = Buffer.from(await parserResponse.arrayBuffer());
  assert.equal(parserResponse.headers.get("x-content-sha256"), PINNED_GFM_PARSER.sha256);
  assert.equal(sha256(parserBytes), PINNED_GFM_PARSER.sha256);
  assert.equal(replay.parser.version, "18.0.5");
  assert.equal(replay.parser.license, "MIT");

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
