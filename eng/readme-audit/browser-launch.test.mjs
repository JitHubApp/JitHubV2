import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { setTimeout as delay } from "node:timers/promises";
import { waitForDevToolsPort } from "./browser-launch.mjs";

test("a successful Edge launcher handoff may publish DevTools later", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "jithub-edge-launch-test-"));
  const portFile = path.join(directory, "DevToolsActivePort");
  try {
    await writeFile(portFile, "12345\n");
    assert.equal(await waitForDevToolsPort(portFile, { exitCode: 0 }, () => ""), "12345\n");

    await writeFile(portFile, "");
    const delayed = (async () => {
      await delay(20);
      await writeFile(portFile, "54321\n");
    })();
    const [port] = await Promise.all([
      waitForDevToolsPort(portFile, { exitCode: 0 }, () => "", 500, 5),
      delayed,
    ]);
    assert.equal(
      port,
      "54321\n");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a nonzero Edge launch failure reports current stderr without waiting", async () => {
  const file = path.join(os.tmpdir(), `jithub-missing-port-${process.pid}`);
  let stderr = "old error";
  stderr = "launch denied";
  await assert.rejects(
    waitForDevToolsPort(file, { exitCode: 7 }, () => stderr, 500, 5),
    /Edge exited before DevTools became ready \(7\)\. launch denied/u);
});

test("a successful handoff without a child port still fails closed", async () => {
  const file = path.join(os.tmpdir(), `jithub-missing-port-${process.pid}`);
  await assert.rejects(
    waitForDevToolsPort(file, { exitCode: 0 }, () => "no port", 35, 5),
    /Edge exited before DevTools became ready \(0\)\. no port/u);
});
