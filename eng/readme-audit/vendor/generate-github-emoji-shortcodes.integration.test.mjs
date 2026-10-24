import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { link, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const vendorDirectory = path.dirname(fileURLToPath(import.meta.url));
const generatorPath = path.join(vendorDirectory, "generate-github-emoji-shortcodes.mjs");

test("integration: CLI rejects altered upstream bytes and source-path aliases", async t => {
  const temporaryDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-emoji-generator-integration-"));
  t.after(() => rm(temporaryDirectory, { recursive: true, force: true }));
  const inputPath = path.join(temporaryDirectory, "emoji.json");
  const outputPath = path.join(temporaryDirectory, "generated.json");
  await writeFile(inputPath, Buffer.from("[]\n", "utf8"));

  const changedSource = spawnSync(process.execPath, [generatorPath, inputPath, outputPath], {
    encoding: "utf8",
    windowsHide: true,
    timeout: 30_000,
  });
  assert.ifError(changedSource.error);
  assert.equal(changedSource.signal, null);
  assert.ok(Number.isInteger(changedSource.status));
  assert.notEqual(changedSource.status, 0);
  assert.match(changedSource.stderr, /does not match pinned/u);
  assert.equal(await readFile(inputPath, "utf8"), "[]\n");

  const overwriteAttempt = spawnSync(process.execPath, [generatorPath, inputPath, inputPath], {
    encoding: "utf8",
    windowsHide: true,
    timeout: 30_000,
  });
  assert.ifError(overwriteAttempt.error);
  assert.equal(overwriteAttempt.signal, null);
  assert.ok(Number.isInteger(overwriteAttempt.status));
  assert.notEqual(overwriteAttempt.status, 0);
  assert.match(overwriteAttempt.stderr, /must not overwrite/u);
  assert.equal(await readFile(inputPath, "utf8"), "[]\n");

  const hardlinkPath = path.join(temporaryDirectory, "hardlink.json");
  await link(inputPath, hardlinkPath);
  const hardlinkAttempt = spawnSync(process.execPath, [generatorPath, inputPath, hardlinkPath], {
    encoding: "utf8",
    windowsHide: true,
    timeout: 30_000,
  });
  assert.ifError(hardlinkAttempt.error);
  assert.equal(hardlinkAttempt.signal, null);
  assert.ok(Number.isInteger(hardlinkAttempt.status));
  assert.notEqual(hardlinkAttempt.status, 0);
  assert.match(hardlinkAttempt.stderr, /must not overwrite/u);
  assert.equal(await readFile(inputPath, "utf8"), "[]\n");

  if (process.platform === "win32") {
    const caseVariantAttempt = spawnSync(process.execPath, [generatorPath, inputPath, inputPath.toUpperCase()], {
      encoding: "utf8",
      windowsHide: true,
      timeout: 30_000,
    });
    assert.ifError(caseVariantAttempt.error);
    assert.equal(caseVariantAttempt.signal, null);
    assert.ok(Number.isInteger(caseVariantAttempt.status));
    assert.notEqual(caseVariantAttempt.status, 0);
    assert.match(caseVariantAttempt.stderr, /must not overwrite/u);
    assert.equal(await readFile(inputPath, "utf8"), "[]\n");

    let uppercaseResolvesToSameGenerator = false;
    try {
      const originalInfo = await stat(generatorPath, { bigint: true });
      const uppercaseInfo = await stat(generatorPath.toUpperCase(), { bigint: true });
      uppercaseResolvesToSameGenerator = originalInfo.dev === uppercaseInfo.dev && originalInfo.ino === uppercaseInfo.ino;
    } catch {
      // A case-sensitive directory may not resolve an uppercase spelling.
    }
    if (uppercaseResolvesToSameGenerator) {
      const caseVariantOutputPath = path.join(temporaryDirectory, "case-variant-script-output.json");
      const extension = path.extname(generatorPath);
      const caseVariantScriptPath = `${generatorPath.slice(0, -extension.length).toUpperCase()}${extension}`;
      const caseVariantScriptAttempt = spawnSync(process.execPath, [caseVariantScriptPath, inputPath, caseVariantOutputPath], {
        encoding: "utf8",
        windowsHide: true,
        timeout: 30_000,
      });
      assert.ifError(caseVariantScriptAttempt.error);
      assert.equal(caseVariantScriptAttempt.signal, null);
      assert.ok(Number.isInteger(caseVariantScriptAttempt.status));
      assert.notEqual(caseVariantScriptAttempt.status, 0, "same-file case-variant script paths must still execute CLI validation");
      assert.match(caseVariantScriptAttempt.stderr, /does not match pinned/u);
      assert.equal(await readFile(inputPath, "utf8"), "[]\n");
      await assert.rejects(readFile(caseVariantOutputPath), { code: "ENOENT" });
    } else {
      t.diagnostic("Uppercase CLI path did not identify the same filesystem object; case-sensitive directories are not aliased.");
    }
  }
});
