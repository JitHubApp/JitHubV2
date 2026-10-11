import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";
import {
  captureVerifiedMarkdownImageAliases,
} from "./verified-source-image-aliases.mjs";

const repository = { fullName: "example/repo" };
const commitSha = "0123456789012345678901234567890123456789";
const pngBytes = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP6zwAAAgcBApocMXEAAAAASUVORK5CYII=", "base64");
const sourceUrl = `https://raw.githubusercontent.com/example/repo/${commitSha}/docs/source.png`;

test("aliases a relative Markdown source only when its bytes match the browser capture", async () => {
  const browserUrl = `https://raw.githubusercontent.com/example/repo/${commitSha}/docs/browser-route.png`;
  const capturedAsset = {
    sha256: sha256(pngBytes),
    bytes: pngBytes,
    mimeType: "image/png",
    urlHashes: [sha256(browserUrl)],
  };
  const result = await captureVerifiedMarkdownImageAliases({
    sources: ["docs/source.png"],
    repository,
    commitSha,
    readmePath: "README.md",
    assets: [capturedAsset],
    fetchImpl: async (url, options) => {
      assert.equal(url, sourceUrl);
      assert.equal(options.redirect, "manual");
      assert.ok(options.signal instanceof AbortSignal);
      return new Response(pngBytes, {
        status: 200,
        headers: { "content-type": "application/octet-stream" },
      });
    },
  });

  assert.equal(result.fetchedCount, 1);
  assert.equal(result.reusedCount, 0);
  assert.equal(result.skippedCount, 0);
  assert.equal(result.assets.length, 1);
  assert.equal(result.assets[0].sha256, "93ae7d494fad0fb30cbf3ae746a39c4bc7a0f8bbf87fbb587a3f3c01f3c5ce20");
  assert.deepEqual(result.assets[0].urlHashes, [sha256(browserUrl), sha256(sourceUrl)].sort());
  assert.equal(JSON.stringify(result).includes("raw.githubusercontent.com"), false);
});

test("reuses browser bytes only for an exact normalized URL alias", async () => {
  const asset = {
    sha256: sha256(pngBytes),
    bytes: pngBytes,
    mimeType: "image/png",
    urlHashes: [sha256(sourceUrl)],
  };
  let fetchCount = 0;
  const result = await captureVerifiedMarkdownImageAliases({
    sources: ["docs/source.png"],
    repository,
    commitSha,
    readmePath: "README.md",
    assets: [asset],
    fetchImpl: async () => {
      fetchCount++;
      throw new Error("exact captured source must not be fetched again");
    },
  });

  assert.equal(fetchCount, 0);
  assert.equal(result.reusedCount, 1);
  assert.equal(result.fetchedCount, 0);
  assert.equal(result.assets.length, 1);
  assert.equal(result.assets[0].urlHashes.length, 1);
});

test("does not add source bytes when they differ from every browser-captured payload", async () => {
  const browserUrl = `https://raw.githubusercontent.com/example/repo/${commitSha}/docs/browser-route.png`;
  const capturedAsset = {
    sha256: sha256(pngBytes),
    bytes: pngBytes,
    mimeType: "image/png",
    urlHashes: [sha256(browserUrl)],
  };
  const differentImage = Buffer.from("GIF89a" + "not the same browser payload", "ascii");
  const result = await captureVerifiedMarkdownImageAliases({
    sources: ["docs/source.png"],
    repository,
    commitSha,
    readmePath: "README.md",
    assets: [capturedAsset],
    fetchImpl: async () => new Response(differentImage, { status: 200 }),
  });

  assert.equal(result.fetchedCount, 1);
  assert.equal(result.byteMismatchCount, 1);
  assert.equal(result.skippedCount, 1);
  assert.equal(result.assets.length, 1);
  assert.deepEqual(result.assets[0].urlHashes, [sha256(browserUrl)]);
});

test("never fetches an untrusted external Markdown image host", async () => {
  let fetchCount = 0;
  const result = await captureVerifiedMarkdownImageAliases({
    sources: ["https://images.example.invalid/logo.png"],
    repository,
    commitSha,
    readmePath: "README.md",
    assets: [],
    fetchImpl: async () => {
      fetchCount++;
      throw new Error("untrusted host must not be requested");
    },
  });

  assert.equal(fetchCount, 0);
  assert.equal(result.skippedCount, 1);
  assert.equal(result.assets.length, 0);
});

test("does not follow a GitHub redirect outside the GitHub host set", async () => {
  const requested = [];
  const result = await captureVerifiedMarkdownImageAliases({
    sources: ["https://github.com/user-attachments/assets/example"],
    repository,
    commitSha,
    readmePath: "README.md",
    assets: [],
    fetchImpl: async url => {
      requested.push(url);
      return new Response(null, {
        status: 302,
        headers: { location: "https://attacker.example/image.png" },
      });
    },
  });

  assert.deepEqual(requested, ["https://github.com/user-attachments/assets/example"]);
  assert.equal(result.skippedCount, 1);
  assert.equal(result.assets.length, 0);
});

test("follows only GitHub's signed user-asset redirect and aliases only the authored URL", async () => {
  const sourceUrl = "https://github.com/example/repo/assets/23818/6b0ae86c-1b0f-4598-a578-c7b62205b3e2";
  const assetUrl = "https://github-production-user-asset-6210df.s3.amazonaws.com/23818/539284821-6b0ae86c-1b0f-4598-a578-c7b62205b3e2.jpg?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIAVCODYLSA53PQK4ZA%2F20260929%2Fus-east-1%2Fs3%2Faws4_request&X-Amz-Date=20260929T021617Z&X-Amz-Expires=300&X-Amz-Signature=366a9e74a9dcf7ba59381067a8b62062c56ca6184495d52b172cae2a469c1c79&X-Amz-SignedHeaders=host&response-content-type=image%2Fjpeg";
  const sourceSha = sha256(sourceUrl);
  const browserUrl = `https://raw.githubusercontent.com/example/repo/${commitSha}/docs/browser.jpg`;
  const capturedAsset = {
    sha256: sha256(pngBytes),
    bytes: pngBytes,
    mimeType: "image/png",
    urlHashes: [sha256(browserUrl)],
  };
  const requested = [];
  const result = await captureVerifiedMarkdownImageAliases({
    sources: [sourceUrl],
    repository,
    commitSha,
    readmePath: "README.md",
    assets: [capturedAsset],
    fetchImpl: async url => {
      requested.push(url);
      if (url === sourceUrl) {
        return new Response(null, { status: 302, headers: { location: assetUrl } });
      }
      assert.equal(url, assetUrl);
      return new Response(pngBytes, { status: 200, headers: { "content-type": "image/png" }, url });
    },
  });

  assert.deepEqual(requested, [sourceUrl, assetUrl]);
  assert.equal(result.fetchedCount, 1);
  assert.equal(result.byteMismatchCount, 0);
  assert.ok(result.assets[0].urlHashes.includes(sourceSha));
  assert.equal(result.assets[0].urlHashes.includes(sha256(assetUrl)), false);
});

test("per-case source alias work has a fixed fetch and byte budget", async () => {
  const sources = Array.from({ length: 20 }, (_, index) =>
    `https://github.com/user-attachments/assets/00000000-0000-4000-8000-${String(index).padStart(12, "0")}`);
  const browserUrl = `https://raw.githubusercontent.com/example/repo/${commitSha}/docs/browser.png`;
  const asset = {
    sha256: sha256(pngBytes),
    bytes: pngBytes,
    mimeType: "image/png",
    urlHashes: [sha256(browserUrl)],
  };
  let fetchCount = 0;
  const result = await captureVerifiedMarkdownImageAliases({
    sources,
    repository,
    commitSha,
    readmePath: "README.md",
    assets: [asset],
    fetchImpl: async () => {
      fetchCount++;
      return new Response(pngBytes, { status: 200, headers: { "content-type": "image/png" } });
    },
  });

  assert.equal(fetchCount, 16);
  assert.equal(result.sourceFetchAttemptCount, 16);
  assert.equal(result.sourceFetchBytes, 16 * pngBytes.length);
  assert.equal(result.sourceFetchBudgetExhausted, true);
});

test("rejects oversized, malformed, and non-image source payloads without creating aliases", async () => {
  const result = await captureVerifiedMarkdownImageAliases({
    sources: ["docs/text.txt"],
    repository,
    commitSha,
    readmePath: "README.md",
    assets: [],
    fetchImpl: async () => new Response("not an image", { status: 200 }),
  });
  assert.equal(result.skippedCount, 1);
  assert.deepEqual(result.assets, []);

  await assert.rejects(captureVerifiedMarkdownImageAliases({
    sources: [],
    repository,
    commitSha: "branch-name",
    readmePath: "README.md",
    assets: [],
  }), /validated repository commit/u);
});

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}
