import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import test from "node:test";
import {
  createSourceReplayFailureReport,
  launchSourceReplay,
} from "./same-byte-edge-source.mjs";

function createLaunchFixture(failureStage) {
  const calls = [];
  const child = new EventEmitter();
  child.stderr = new EventEmitter();
  child.stderr.setEncoding = () => {};
  child.kill = () => { calls.push("kill-owned-child"); };
  const connection = {
    send: async method => {
      calls.push(method);
      if (failureStage === "enable-devtools" && method === "Runtime.enable") {
        throw new Error("Timed out waiting for Edge DevTools command Runtime.enable.");
      }
    },
    close: () => { calls.push("close-owned-connection"); },
  };
  const dependencies = {
    spawnProcess: () => {
      if (failureStage === "launch-edge") throw new Error("Edge could not start.");
      return child;
    },
    waitForPort: async (_file, actualChild, _stderr, timeoutMs, pollMs, getSpawnError) => {
      assert.equal(actualChild, child);
      assert.equal(timeoutMs, 20_000);
      assert.equal(pollMs, 50);
      if (failureStage === "async-spawn-error") {
        const error = new Error("Edge could not start.");
        child.emit("error", error);
        assert.equal(getSpawnError(), error);
        throw error;
      }
      if (failureStage === "wait-devtools-port") throw new Error("Timed out waiting for Edge DevTools.");
      return "12345\n";
    },
    fetchTargets: async url => {
      assert.equal(url, "http://127.0.0.1:12345/json/list");
      if (failureStage === "read-devtools-targets") throw new Error("Edge DevTools target request timed out.");
      return { json: async () => [{ type: "page", webSocketDebuggerUrl: "ws://127.0.0.1:12345/page" }] };
    },
    connect: async () => {
      if (failureStage === "connect-devtools") throw new Error("Timed out connecting to Edge DevTools.");
      return connection;
    },
  };
  return { calls, child, connection, dependencies };
}

for (const stage of ["launch-edge", "wait-devtools-port", "read-devtools-targets", "connect-devtools", "enable-devtools"]) {
  test(`source replay releases partial startup after ${stage} fails without retrying`, async () => {
    const { calls, dependencies } = createLaunchFixture(stage);
    await assert.rejects(launchSourceReplay({ edgePath: "edge", profileDirectory: "generated-profile" }, dependencies), error => {
      assert.equal(error.sourceReplayStage, stage);
      assert.equal(createSourceReplayFailureReport(error).failureStage, stage);
      return true;
    });
    assert.equal(calls.filter(value => value === "kill-owned-child").length, stage === "launch-edge" ? 0 : 1);
    assert.equal(calls.filter(value => value === "close-owned-connection").length, stage === "enable-devtools" ? 1 : 0);
    if (stage === "enable-devtools") assert.deepEqual(calls.slice(-2), ["close-owned-connection", "kill-owned-child"]);
  });
}

test("an asynchronous process startup error is handled and retains cleanup ownership", async () => {
  const { calls, dependencies } = createLaunchFixture("async-spawn-error");
  await assert.rejects(launchSourceReplay({ edgePath: "edge", profileDirectory: "generated-profile" }, dependencies), /Edge could not start/u);
  assert.deepEqual(calls, ["kill-owned-child"]);
});

test("successful startup transfers both resources to the caller", async () => {
  const { calls, child, connection, dependencies } = createLaunchFixture();
  const result = await launchSourceReplay({ edgePath: "edge", profileDirectory: "generated-profile" }, dependencies);
  assert.equal(result.edge, child);
  assert.equal(result.cdp, connection);
  assert.deepEqual(calls, ["Page.enable", "Runtime.enable", "Network.enable"]);
});

test("a native spawn error is categorized by startup stage without exposing its path", async () => {
  const { dependencies } = createLaunchFixture();
  dependencies.spawnProcess = () => {
    throw Object.assign(new Error("spawn C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe ENOENT"),
      { code: "ENOENT" });
  };
  await assert.rejects(launchSourceReplay({ edgePath: "edge", profileDirectory: "generated-profile" }, dependencies), error => {
    const report = createSourceReplayFailureReport(error);
    assert.equal(report.failureCategory, "browser-startup");
    assert.equal(report.failureStage, "launch-edge");
    assert.doesNotMatch(JSON.stringify(report), /Program Files|msedge\.exe|ENOENT/u);
    return true;
  });
});

test("cleanup errors do not replace the original startup failure", async () => {
  const { child, connection, dependencies } = createLaunchFixture("enable-devtools");
  child.kill = () => { throw new Error("kill failed"); };
  connection.close = () => { throw new Error("close failed"); };
  await assert.rejects(launchSourceReplay({ edgePath: "edge", profileDirectory: "generated-profile" }, dependencies),
    /Timed out waiting for Edge DevTools command Runtime.enable/u);
});

test("a non-Error rejection retains the startup stage without exposing its value", async () => {
  const { calls, dependencies } = createLaunchFixture();
  dependencies.fetchTargets = async () => { throw "private startup detail"; };
  await assert.rejects(launchSourceReplay({ edgePath: "edge", profileDirectory: "generated-profile" }, dependencies), error => {
    assert.equal(error.message, "Edge startup failed.");
    assert.equal(error.sourceReplayStage, "read-devtools-targets");
    assert.doesNotMatch(JSON.stringify(createSourceReplayFailureReport(error)), /private startup detail/u);
    return true;
  });
  assert.deepEqual(calls, ["kill-owned-child"]);
});
