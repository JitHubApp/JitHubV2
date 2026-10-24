import assert from "node:assert/strict";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import {
  BrowserProfileCleanupError,
  cleanupBrowserProfile,
} from "./browser-profile-cleanup.mjs";

const profileDirectory = path.join(os.tmpdir(), "jithub-readme-edge-AbC123");

test("stop failure preserves the profile, skips removal, and still closes servers", async () => {
  const calls = [];
  await assert.rejects(cleanupBrowserProfile(profileDirectory, {
    stopProcesses: async value => { calls.push(["stop", value]); throw new Error("private path/detail"); },
    removeProfile: async () => { calls.push(["remove"]); },
    closeServers: async () => { calls.push(["close-servers"]); },
    temporaryDirectory: os.tmpdir(),
  }), error => {
    assert.ok(error instanceof BrowserProfileCleanupError);
    assert.equal(error.failureStage, "stop-profile-processes");
    assert.equal(error.profileState, "preserved");
    assert.equal(error.profilePreserved, true);
    assert.equal(error.serverCloseFailed, false);
    assert.doesNotMatch(error.message, /private path|AbC123/u);
    return true;
  });
  assert.deepEqual(calls.map(([stage]) => stage), ["stop", "close-servers"]);
});

test("removal failure fails closed after process stop", async () => {
  const calls = [];
  await assert.rejects(cleanupBrowserProfile(profileDirectory, {
    stopProcesses: async () => { calls.push("stop"); },
    removeProfile: async () => { calls.push("remove"); throw new Error("private removal detail"); },
    closeServers: async () => { calls.push("close-servers"); },
    temporaryDirectory: os.tmpdir(),
  }), error => {
    assert.equal(error.failureStage, "remove-profile");
    assert.equal(error.profileState, "unknown");
    assert.equal(error.profilePreserved, null);
    assert.equal(error.serverCloseFailed, false);
    assert.doesNotMatch(error.message, /private removal detail|AbC123/u);
    return true;
  });
  assert.deepEqual(calls, ["stop", "remove", "close-servers"]);
});

test("successful cleanup strictly orders process stop, verified removal, and server closure", async () => {
  const calls = [];
  const result = await cleanupBrowserProfile(profileDirectory, {
    stopProcesses: async value => { calls.push(["stop", value]); },
    removeProfile: async value => { calls.push(["remove", value]); },
    closeServers: async () => { calls.push(["close-servers"]); },
    temporaryDirectory: os.tmpdir(),
  });
  assert.deepEqual(calls.map(([stage]) => stage), ["stop", "remove", "close-servers"]);
  assert.deepEqual(result, { profileState: "removed", serversClosed: true });
});

test("a server-close failure is reported after profile cleanup completes", async () => {
  const calls = [];
  await assert.rejects(cleanupBrowserProfile(profileDirectory, {
    stopProcesses: async () => { calls.push("stop"); },
    removeProfile: async () => { calls.push("remove"); },
    closeServers: async () => { calls.push("close-servers"); throw new Error("private server detail"); },
    temporaryDirectory: os.tmpdir(),
  }), error => {
    assert.equal(error.failureStage, "close-servers");
    assert.equal(error.profileState, "removed");
    assert.equal(error.profilePreserved, false);
    assert.equal(error.serverCloseFailed, true);
    assert.doesNotMatch(error.message, /private server detail|AbC123/u);
    return true;
  });
  assert.deepEqual(calls, ["stop", "remove", "close-servers"]);
});

test("a registered test after-hook rejects on scoped cleanup failure", async () => {
  let registeredHook;
  const fakeTestContext = { after(callback) { registeredHook = callback; } };
  fakeTestContext.after(() => cleanupBrowserProfile(profileDirectory, {
    stopProcesses: async () => { throw new Error("private stop detail"); },
    removeProfile: async () => assert.fail("profile must not be removed after stop failure"),
    closeServers: async () => {},
    temporaryDirectory: os.tmpdir(),
  }));
  assert.equal(typeof registeredHook, "function");
  await assert.rejects(registeredHook, error => error.failureStage === "stop-profile-processes");
});

test("invalid profile scope skips process and removal callbacks but still closes servers", async () => {
  const calls = [];
  await assert.rejects(cleanupBrowserProfile(path.join(os.tmpdir(), "unrelated"), {
    stopProcesses: async () => { calls.push("stop"); },
    removeProfile: async () => { calls.push("remove"); },
    closeServers: async () => { calls.push("close-servers"); },
    temporaryDirectory: os.tmpdir(),
  }), error => error.failureStage === "validate-profile");
  assert.deepEqual(calls, ["close-servers"]);
});

test("a case-only parent-path variant is rejected before process or removal callbacks", async () => {
  const expectedParent = path.join(os.tmpdir(), "AuditCaseParent");
  const alternateParent = path.join(os.tmpdir(), "auditcaseparent");
  assert.notEqual(path.resolve(alternateParent), path.resolve(expectedParent));
  const calls = [];
  await assert.rejects(cleanupBrowserProfile(path.join(alternateParent, "jithub-readme-edge-AbC123"), {
    stopProcesses: async () => { calls.push("stop"); },
    removeProfile: async () => { calls.push("remove"); },
    closeServers: async () => { calls.push("close-servers"); },
    temporaryDirectory: expectedParent,
  }), error => error.failureStage === "validate-profile");
  assert.deepEqual(calls, ["close-servers"]);
});
