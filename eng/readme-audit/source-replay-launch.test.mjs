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
  child.exitCode = null;
  child.signalCode = null;
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
      const report = createSourceReplayFailureReport(error);
      assert.equal(report.failureStage, stage);
      if (stage !== "wait-devtools-port") assert.equal("startupDiagnostics" in report, false);
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

test("DevTools-port timeout preserves bounded redacted Edge startup evidence", async () => {
  const { child, dependencies } = createLaunchFixture("wait-devtools-port");
  child.exitCode = -1_073_741_819;
  child.kill = () => { child.signalCode = "SIGTERM"; };
  dependencies.waitForPort = async () => {
    child.stderr.emit("data", `${"x".repeat(9000)}\nERROR edge startup failed at C:\\Users\\private\\Temp\\edge-profile; C:/Users/private/Edge/msedge.exe; \\\\server\\share\\private\\file; JSON C:\\\\Users\\\\private\\\\profile; https://secret.example/token\n`);
    throw new Error("Timed out waiting for Edge DevTools.");
  };

  await assert.rejects(
    launchSourceReplay({
      edgePath: "C:\\Program Files\\Microsoft\\Edge\\msedge.exe",
      profileDirectory: "C:\\Users\\private\\Temp\\edge-profile",
    }, dependencies),
    error => {
      const report = createSourceReplayFailureReport(error);
      assert.equal(report.failureCategory, "timeout");
      assert.equal(report.failureStage, "wait-devtools-port");
      assert.equal(report.error, "The source-bound Markdown replay exceeded its deadline.");
      assert.deepEqual({
        processExitCode: report.startupDiagnostics.processExitCode,
        processSignalCode: report.startupDiagnostics.processSignalCode,
        spawnErrorCode: report.startupDiagnostics.spawnErrorCode,
        devToolsPortFilePresent: report.startupDiagnostics.devToolsPortFilePresent,
      }, {
        processExitCode: -1_073_741_819,
        processSignalCode: null,
        spawnErrorCode: null,
        devToolsPortFilePresent: false,
      });
      assert.ok(report.startupDiagnostics.stderrTail.length <= 2048);
      assert.match(report.startupDiagnostics.stderrTail, /ERROR edge startup failed at <path>/u);
      assert.match(report.startupDiagnostics.stderrTail, /sha256:[0-9a-f]{64}/u);
      assert.doesNotMatch(JSON.stringify(report), /private|secret\.example|msedge\.exe|generated-profile|C:[\\/]|\\\\server/u);
      return true;
    },
  );
  assert.equal(child.signalCode, "SIGTERM", "the diagnostic reports process state before owned-child cleanup mutates it");
});

test("startup diagnostics reject invalid fields and are omitted for non-startup failures", () => {
  const error = Object.assign(new Error("startup timed out"), {
    sourceReplayStage: "wait-devtools-port",
    sourceReplayStartupDiagnostics: {
      processExitCode: 4_294_967_296,
      processSignalCode: "private signal",
      spawnErrorCode: "C:\\private\\code",
      devToolsPortFilePresent: "true",
      stderrTail: "failure at C:/private/profile; \\\\server\\private\\item",
    },
  });
  const report = createSourceReplayFailureReport(error);
  assert.deepEqual(report.startupDiagnostics, {
    processExitCode: null,
    processSignalCode: null,
    spawnErrorCode: null,
    devToolsPortFilePresent: false,
    stderrTail: "failure at <path>; <path>",
  });
});

test("unsigned Windows NTSTATUS exit codes survive startup diagnostic sanitization", () => {
  const report = createSourceReplayFailureReport(Object.assign(new Error("Edge startup failed"), {
    sourceReplayStage: "wait-devtools-port",
    sourceReplayStartupDiagnostics: {
      processExitCode: 0xC0000005,
      processSignalCode: null,
      spawnErrorCode: null,
      devToolsPortFilePresent: false,
      stderrTail: "",
    },
  }));
  assert.equal(report.startupDiagnostics.processExitCode, 0xC0000005);
});

test("frozen startup errors are normalized only after owned-child cleanup", async () => {
  const { calls, child, dependencies } = createLaunchFixture("wait-devtools-port");
  const original = Object.freeze(new Error("frozen startup error"));
  dependencies.waitForPort = async () => { throw original; };

  await assert.rejects(
    launchSourceReplay({ edgePath: "edge", profileDirectory: "generated-profile" }, dependencies),
    error => {
      assert.notEqual(error, original, "metadata is attached to a wrapper when the original Error is frozen");
      assert.equal(error.message, original.message);
      assert.equal(error.name, original.name);
      assert.equal(error.cause, original);
      assert.equal(error.sourceReplayStage, "wait-devtools-port");
      assert.equal(createSourceReplayFailureReport(error).failureStage, "wait-devtools-port");
      return true;
    },
  );
  assert.deepEqual(calls, ["kill-owned-child"]);
  assert.equal(child.signalCode, null);
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
