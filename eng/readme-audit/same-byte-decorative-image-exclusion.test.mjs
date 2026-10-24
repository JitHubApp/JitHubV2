import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import {
  captureSameByteCorpus,
  createNonRenderedDecorativeImageExclusionEntries,
  createResponseRecorder,
  createSameByteReplayServer,
  gitBlobSha1,
  resourceUrlSha256,
  sha256,
} from "./same-byte-corpus.mjs";
import { createUniqueCapturedDecorativeImageExclusionMap } from "./same-byte-edge-source.mjs";

const commitSha = "0123456789012345678901234567890123456789";
const readmeUrl = `https://raw.githubusercontent.com/example/repo/${commitSha}/README.md`;
const visibleImageUrl = `https://raw.githubusercontent.com/example/repo/${commitSha}/docs/image.png`;
const hiddenImageUrl = "https://travis-ci.org/example/repo.svg?branch=main";
const imageBytes = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3]);

function createCaptureCdp() {
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
      assert.equal(params.requestId, "visible-image");
      return { body: imageBytes.toString("base64"), base64Encoded: true };
    },
  };
}

function emitVisibleImageResponse(cdp) {
  cdp.emit("Network.requestWillBeSent", {
    requestId: "visible-image",
    request: { url: visibleImageUrl },
  });
  cdp.emit("Network.responseReceived", {
    requestId: "visible-image",
    type: "Image",
    response: { url: visibleImageUrl, status: 200, mimeType: "image/png" },
  });
  cdp.emit("Network.loadingFinished", {
    requestId: "visible-image",
    encodedDataLength: imageBytes.length,
  });
}

const decorativeEvidence = overrides => ({
  replayIndex: 3,
  source: hiddenImageUrl,
  currentSource: hiddenImageUrl,
  canonicalSource: hiddenImageUrl,
  explicitEmptyAlt: true,
  failedDecode: true,
  zeroBounds: true,
  ...overrides,
});

test("decorative-image proof requires all live non-rendered facts and persists only URL hashes", () => {
  const entries = createNonRenderedDecorativeImageExclusionEntries(
    [decorativeEvidence({ source: `${hiddenImageUrl}#badge` })],
    readmeUrl,
  );
  assert.deepEqual(entries, [{
    replayIndex: 3,
    urlSha256s: [resourceUrlSha256(hiddenImageUrl)],
    explicitEmptyAlt: true,
    failedDecode: true,
    zeroBounds: true,
  }]);
  assert.equal(JSON.stringify(entries).includes(hiddenImageUrl), false);

  for (const field of ["explicitEmptyAlt", "failedDecode", "zeroBounds"]) {
    assert.throws(() => createNonRenderedDecorativeImageExclusionEntries(
      [decorativeEvidence({ [field]: false })],
      readmeUrl,
    ), /unproven or ambiguous/u);
  }
  assert.throws(() => createNonRenderedDecorativeImageExclusionEntries(
    [decorativeEvidence({ replayIndex: 3 }), decorativeEvidence({ replayIndex: 3 })],
    readmeUrl,
  ), /unproven or ambiguous/u);
  assert.throws(() => createNonRenderedDecorativeImageExclusionEntries(
    [decorativeEvidence({ source: "data:image/png;base64,AA==", currentSource: "", canonicalSource: "" })],
    readmeUrl,
  ), /bounded HTTP source identity/u);
});

test("source exclusion requires a unique exact URL hash and never hides named or ambiguous images", () => {
  const hiddenHash = "a".repeat(64);
  const otherHash = "b".repeat(64);
  const proof = {
    urlSha256s: [hiddenHash],
    explicitEmptyAlt: true,
    failedDecode: true,
    zeroBounds: true,
  };

  assert.deepEqual([...createUniqueCapturedDecorativeImageExclusionMap([
    { explicitEmptyAlt: true, candidateUrlSha256s: [hiddenHash] },
    { explicitEmptyAlt: false, candidateUrlSha256s: [hiddenHash] },
    { explicitEmptyAlt: true, candidateUrlSha256s: [otherHash] },
  ], [proof])], [0]);
  assert.equal(createUniqueCapturedDecorativeImageExclusionMap([
    { explicitEmptyAlt: true, candidateUrlSha256s: [otherHash] },
  ], [proof]), null, "a different URL cannot be excluded by a proof");
  assert.equal(createUniqueCapturedDecorativeImageExclusionMap([
    { explicitEmptyAlt: true, candidateUrlSha256s: [hiddenHash] },
    { explicitEmptyAlt: true, candidateUrlSha256s: [hiddenHash] },
  ], [proof]), null, "duplicate source URLs fail closed instead of falling back by ordinal");
  assert.equal(createUniqueCapturedDecorativeImageExclusionMap([
    { explicitEmptyAlt: true, candidateUrlSha256s: [hiddenHash] },
  ], [{ ...proof, failedDecode: false }]), null, "incomplete live evidence is rejected");
});

test("captured corpus binds a hidden decorative-image proof without changing visible routes or byte checks", async t => {
  const directory = path.join(await mkdtemp(path.join(os.tmpdir(), "jithub-decorative-image-")), "case");
  t.after(() => rm(path.dirname(directory), { recursive: true, force: true }));
  const readmeBytes = Buffer.from(`![visible](docs/image.png)\n![](${hiddenImageUrl})\n`, "utf8");
  const cdp = createCaptureCdp();
  const responseRecorder = createResponseRecorder(cdp);
  emitVisibleImageResponse(cdp);
  try {
    await captureSameByteCorpus({
      directory,
      repository: { fullName: "example/repo", commitSha },
      readmeUrl,
      readmePath: "README.md",
      readmeGitBlobSha1: gitBlobSha1(readmeBytes),
      readmeByteSize: readmeBytes.length,
      images: [{
        source: visibleImageUrl,
        currentSource: visibleImageUrl,
        replayIndex: 0,
        complete: true,
        naturalWidth: 1,
        naturalHeight: 1,
      }],
      nonRenderedDecorativeImages: [decorativeEvidence({})],
      renderedHtml: '<article class="markdown-body"><img alt="visible" data-jithub-image-index="0"></article>',
      responseRecorder,
      fetchImpl: async () => new Response(readmeBytes, { status: 200 }),
    });
  } finally {
    responseRecorder.dispose();
  }

  const manifestBytes = await readFile(path.join(directory, "manifest.json"));
  const manifestText = manifestBytes.toString("utf8");
  const manifest = JSON.parse(manifestText);
  assert.equal(manifest.readme.sha256, sha256(readmeBytes));
  assert.equal(manifest.imageRoutes.length, 1, "visible-image route coverage is unchanged");
  assert.equal(manifest.imageRoutes[0].index, 0);
  assert.deepEqual(manifest.nonRenderedDecorativeImageExclusions, [{
    replayIndex: 3,
    urlSha256s: [resourceUrlSha256(hiddenImageUrl)],
    explicitEmptyAlt: true,
    failedDecode: true,
    zeroBounds: true,
  }]);
  assert.equal(manifestText.includes(hiddenImageUrl), false);

  const replay = await createSameByteReplayServer(directory);
  t.after(replay.close);
  assert.equal(replay.manifest.nonRenderedDecorativeImageExclusions.length, 1);
  assert.equal(manifest.assets.reduce((total, asset) => total + asset.byteSize, 0), imageBytes.length,
    "the visible payload remains subject to the original captured-byte checks");
  assert.equal(manifest.assets[0].sha256, sha256(imageBytes));
});
