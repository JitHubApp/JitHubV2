import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { stopBrowserProfileProcesses } from "./browser-process-lifetime.mjs";

test("profile cleanup rejects broad and non-audit process targets", async () => {
  await assert.rejects(
    stopBrowserProfileProcesses(os.tmpdir()),
    /Refusing to stop Edge processes/u);
  await assert.rejects(
    stopBrowserProfileProcesses(path.join(os.tmpdir(), "jithub-readme-edge-abcd")),
    /Refusing to stop Edge processes/u);
  await assert.rejects(
    stopBrowserProfileProcesses(path.join(os.tmpdir(), "nested", "jithub-readme-edge-abcdef")),
    /Refusing to stop Edge processes/u);
});

test("profile cleanup accepts one generated audit profile", async () => {
  const profile = await mkdtemp(path.join(os.tmpdir(), "jithub-readme-edge-"));
  try {
    await stopBrowserProfileProcesses(profile);
  } finally {
    await rm(profile, { recursive: true, force: true });
  }
});
