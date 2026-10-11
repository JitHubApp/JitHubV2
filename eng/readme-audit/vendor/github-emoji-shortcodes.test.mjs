import assert from "node:assert/strict";
import { statSync } from "node:fs";
import { copyFile, link, mkdtemp, readFile, rm, symlink, unlink, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import vm from "node:vm";
import test from "node:test";
import { loadPinnedGitHubEmojiShortcodes, PINNED_GITHUB_EMOJI_SHORTCODES } from "./github-emoji-shortcodes.mjs";
import {
  generateGitHubEmojiShortcodes,
  isGeneratorDirectInvocation,
} from "./generate-github-emoji-shortcodes.mjs";

const require = createRequire(import.meta.url);
const vendorDirectory = path.dirname(fileURLToPath(import.meta.url));

test("generator main-module detection uses filesystem identity without treating imports as CLI calls", () => {
  const actualGeneratorPath = path.join(vendorDirectory, "generate-github-emoji-shortcodes.mjs");
  assert.equal(isGeneratorDirectInvocation(actualGeneratorPath, actualGeneratorPath), true);
  assert.equal(
    isGeneratorDirectInvocation(fileURLToPath(import.meta.url), actualGeneratorPath),
    false,
    "importing the generator from this test must not be mistaken for direct CLI invocation",
  );
  assert.equal(isGeneratorDirectInvocation(null, actualGeneratorPath), false);

  if (process.platform === "win32") {
    const caseVariantPath = actualGeneratorPath.toUpperCase();
    let resolvesToSameFile = false;
    try {
      const original = statSync(actualGeneratorPath, { bigint: true });
      const variant = statSync(caseVariantPath, { bigint: true });
      resolvesToSameFile = original.dev === variant.dev && original.ino === variant.ino;
    } catch {
      // A case-sensitive directory may reject the uppercase spelling entirely.
    }
    assert.equal(
      isGeneratorDirectInvocation(caseVariantPath, actualGeneratorPath),
      resolvesToSameFile,
      "case aliases must invoke only when both paths resolve to the same filesystem object",
    );
  }
});

test("pinned shortcode data carries immutable source, map, extension, and license identity", async () => {
  const pinned = await loadPinnedGitHubEmojiShortcodes();
  assert.equal(pinned.pins.sourceCommit, "fadaeaf1f1a9be82b321316a6c5502e43138b2f6");
  assert.match(pinned.pins.sourceSha256, /^[0-9a-f]{64}$/u);
  assert.equal(pinned.pins.sourceLicense, "MIT");
  assert.match(pinned.pins.sourceLicenseSha256, /^[0-9a-f]{64}$/u);
  assert.match(pinned.pins.mapSha256, /^[0-9a-f]{64}$/u);
  assert.match(pinned.pins.extensionSha256, /^[0-9a-f]{64}$/u);
  assert.equal(Object.keys(pinned.aliases).length, pinned.pins.aliasCount);
});

test("Marked text tokens expand pinned shortcodes while code, HTML, unknown aliases, and destinations remain literal", async () => {
  const pinned = await loadPinnedGitHubEmojiShortcodes();
  const alias = Object.keys(pinned.aliases)[0];
  const emoji = pinned.aliases[alias];
  const markedModule = require(path.join(vendorDirectory, "marked-18.0.5.umd.js"));
  const marked = new markedModule.Marked({ gfm: true, breaks: false, pedantic: false });
  const sandboxWindow = {};
  vm.runInNewContext(pinned.extensionBytes.toString("utf8"), { window: sandboxWindow });
  assert.equal(sandboxWindow.installPinnedGitHubEmojiShortcodes(marked, pinned.aliases, pinned.pins.aliasCount), true);

  const source = [
    `ordinary :${alias}: text and :unknown_jithub_alias:`,
    `inline \`:${alias}:\``,
    "```text",
    `:${alias}:`,
    "```",
    `<span data-shortcode=":${alias}:">html :${alias}:</span>`,
    `[link :${alias}:](https://example.invalid/:${alias}:)`,
  ].join("\n\n");
  const html = marked.parse(source);
  assert.ok(html.includes(emoji), "ordinary Markdown text should use the pinned Unicode value");
  assert.ok(html.includes(":unknown_jithub_alias:"), "unknown shortcode syntax must remain literal");
  assert.ok(html.includes(`<code>:${alias}:</code>`), "inline code must remain literal");
  assert.ok(html.includes(`:${alias}:\n</code></pre>`), "fenced code must remain literal");
  assert.ok(html.includes(`data-shortcode=":${alias}:"`), "HTML attributes must remain literal");
  assert.ok(html.includes(`https://example.invalid/:${alias}:`), "link destinations must remain literal");
});

for (const fileName of [
  PINNED_GITHUB_EMOJI_SHORTCODES.mapFile,
  PINNED_GITHUB_EMOJI_SHORTCODES.extensionFile,
  PINNED_GITHUB_EMOJI_SHORTCODES.licenseFile,
]) {
  test(`pinned shortcode loader fails closed when ${fileName} is tampered`, async t => {
    const temporaryDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-emoji-shortcode-tamper-"));
    t.after(() => rm(temporaryDirectory, { recursive: true, force: true }));
    for (const requiredFile of [
      PINNED_GITHUB_EMOJI_SHORTCODES.mapFile,
      PINNED_GITHUB_EMOJI_SHORTCODES.extensionFile,
      PINNED_GITHUB_EMOJI_SHORTCODES.licenseFile,
    ]) {
      await copyFile(path.join(vendorDirectory, requiredFile), path.join(temporaryDirectory, requiredFile));
    }
    const targetPath = path.join(temporaryDirectory, fileName);
    const originalBytes = await readFile(targetPath);
    await writeFile(targetPath, Buffer.concat([originalBytes, Buffer.from("\n tampered", "utf8")]));
    await assert.rejects(
      loadPinnedGitHubEmojiShortcodes({ vendorDirectory: temporaryDirectory }),
      /does not match its pinned SHA-256/u,
    );
  });
}

test("pinned shortcode loader rejects oversized and symlinked vendor files", async t => {
  const temporaryDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-emoji-shortcode-bounds-"));
  t.after(() => rm(temporaryDirectory, { recursive: true, force: true }));
  for (const requiredFile of [
    PINNED_GITHUB_EMOJI_SHORTCODES.mapFile,
    PINNED_GITHUB_EMOJI_SHORTCODES.extensionFile,
    PINNED_GITHUB_EMOJI_SHORTCODES.licenseFile,
  ]) {
    await copyFile(path.join(vendorDirectory, requiredFile), path.join(temporaryDirectory, requiredFile));
  }
  const targetPath = path.join(temporaryDirectory, PINNED_GITHUB_EMOJI_SHORTCODES.mapFile);
  await writeFile(targetPath, Buffer.alloc(128 * 1024 + 1));
  await assert.rejects(
    loadPinnedGitHubEmojiShortcodes({ vendorDirectory: temporaryDirectory }),
    /exceeds its 131072-byte bound/u,
  );

  await unlink(targetPath);
  try {
    await symlink(path.join(vendorDirectory, PINNED_GITHUB_EMOJI_SHORTCODES.mapFile), targetPath);
  } catch (error) {
    if (error?.code === "EPERM" || error?.code === "EACCES") {
      t.diagnostic("Symlink creation is unavailable on this machine.");
      return;
    }
    throw error;
  }
  await assert.rejects(
    loadPinnedGitHubEmojiShortcodes({ vendorDirectory: temporaryDirectory }),
    /exceeds its 131072-byte bound/u,
  );
});

test("generator validation rejects altered bytes and source aliases without a child process", async t => {
  const temporaryDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-emoji-generator-tamper-"));
  t.after(() => rm(temporaryDirectory, { recursive: true, force: true }));
  const inputPath = path.join(temporaryDirectory, "emoji.json");
  const outputPath = path.join(temporaryDirectory, "generated.json");
  await writeFile(inputPath, Buffer.from("[]\n", "utf8"));
  await assert.rejects(
    generateGitHubEmojiShortcodes(inputPath, outputPath, { log: () => {} }),
    /does not match pinned/u,
  );
  assert.equal(await readFile(inputPath, "utf8"), "[]\n");
  await assert.rejects(readFile(outputPath), { code: "ENOENT" });

  await assert.rejects(
    generateGitHubEmojiShortcodes(inputPath, inputPath, { log: () => {} }),
    /must not overwrite/u,
  );
  assert.equal(await readFile(inputPath, "utf8"), "[]\n");

  const hardlinkPath = path.join(temporaryDirectory, "hardlink.json");
  await link(inputPath, hardlinkPath);
  await assert.rejects(
    generateGitHubEmojiShortcodes(inputPath, hardlinkPath, { log: () => {} }),
    /must not overwrite/u,
  );
  assert.equal(await readFile(inputPath, "utf8"), "[]\n");

  await assert.rejects(
    generateGitHubEmojiShortcodes(inputPath, inputPath.toUpperCase(), {
      platform: "win32",
      log: () => {},
    }),
    /must not overwrite/u,
  );
  assert.equal(await readFile(inputPath, "utf8"), "[]\n");
});
