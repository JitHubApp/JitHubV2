import assert from "node:assert/strict";
import test from "node:test";
import { DocumentReadinessTimeout, metricDelta, navigateReadme } from "./browser-navigation.mjs";

test("retry metric deltas preserve work after a renderer counter reset", () => {
  assert.equal(metricDelta({ TaskDuration: 10 }, { TaskDuration: 7 }, "TaskDuration"), 3);
  assert.equal(metricDelta({ TaskDuration: 3 }, { TaskDuration: 7 }, "TaskDuration"), 3);
  assert.equal(metricDelta({ TaskDuration: 3 }, {}, "TaskDuration"), 3);
});

function fakeCdp(responses = {}) {
  const calls = [];
  return {
    calls,
    async send(method, arguments_) {
      calls.push({ method, arguments_ });
      return responses[method] || {};
    },
  };
}

test("first interactive document needs no retry", async () => {
  const cdp = fakeCdp();
  const waits = [];
  const result = await navigateReadme(
    cdp,
    "https://github.com/example/repo",
    async (...arguments_) => waits.push(arguments_),
    async () => 123,
    () => 456);

  assert.deepEqual(result, {
    navigationStarted: 456,
    navigationRetries: 0,
    retryMetricBaseline: {},
  });
  assert.deepEqual(cdp.calls.map(call => call.method), ["Page.navigate"]);
  assert.deepEqual(waits, [[cdp, 123, 60_000]]);
});

test("one document-readiness timeout gets one fresh navigation and metric baseline", async () => {
  const cdp = fakeCdp({
    "Performance.getMetrics": {
      metrics: [{ name: "TaskDuration", value: 7 }, { name: "LayoutCount", value: 4 }],
    },
  });
  let waits = 0;
  let clock = 0;
  const result = await navigateReadme(
    cdp,
    "https://github.com/example/repo",
    async () => {
      if (++waits === 1) throw new DocumentReadinessTimeout("stalled");
    },
    async () => waits + 100,
    () => ++clock);

  assert.deepEqual(result, {
    navigationStarted: 2,
    navigationRetries: 1,
    retryMetricBaseline: { TaskDuration: 7, LayoutCount: 4 },
  });
  assert.deepEqual(cdp.calls.map(call => call.method), [
    "Page.navigate", "Page.stopLoading", "Performance.getMetrics", "Page.navigate",
  ]);
  assert.equal(waits, 2);
});

test("a second readiness timeout fails rather than waiving the case", async () => {
  const cdp = fakeCdp({ "Performance.getMetrics": { metrics: [] } });
  await assert.rejects(
    navigateReadme(
      cdp,
      "https://github.com/example/repo",
      async () => { throw new DocumentReadinessTimeout("still stalled"); },
      async () => 123),
    { name: "Error", message: "still stalled" });
  assert.equal(cdp.calls.filter(call => call.method === "Page.navigate").length, 2);
});

test("a non-readiness failure is not retried", async () => {
  const cdp = fakeCdp();
  await assert.rejects(
    navigateReadme(
      cdp,
      "https://github.com/example/repo",
      async () => { throw new Error("broken CDP"); },
      async () => 123),
    { message: "broken CDP" });
  assert.deepEqual(cdp.calls.map(call => call.method), ["Page.navigate"]);
});

test("a navigation error is not reclassified as a document-readiness timeout", async () => {
  const cdp = fakeCdp({ "Page.navigate": { errorText: "blocked" } });
  await assert.rejects(
    navigateReadme(
      cdp,
      "https://github.com/example/repo",
      async () => {},
      async () => 123),
    { message: "Edge navigation failed: blocked" });
  assert.deepEqual(cdp.calls.map(call => call.method), ["Page.navigate"]);
});

test("one Edge buffer-exhaustion navigation gets a fresh attempt and metric baseline", async () => {
  let navigations = 0;
  const calls = [];
  const cdp = {
    async send(method) {
      calls.push(method);
      if (method === "Page.navigate") {
        return ++navigations === 1
          ? { errorText: "net::ERR_NO_BUFFER_SPACE" }
          : {};
      }
      if (method === "Performance.getMetrics") {
        return { metrics: [{ name: "TaskDuration", value: 7 }] };
      }
      return {};
    },
  };
  let waits = 0;
  let clock = 0;
  const pauses = [];

  const result = await navigateReadme(
    cdp,
    "https://github.com/example/repo",
    async () => { waits++; },
    async () => 123,
    () => ++clock,
    async milliseconds => { pauses.push(milliseconds); });

  assert.deepEqual(result, {
    navigationStarted: 2,
    navigationRetries: 1,
    retryMetricBaseline: { TaskDuration: 7 },
  });
  assert.equal(waits, 1);
  assert.deepEqual(pauses, [1_000]);
  assert.deepEqual(calls, [
    "Page.navigate", "Page.stopLoading", "Performance.getMetrics", "Page.navigate",
  ]);
});

test("a second Edge buffer-exhaustion navigation remains a failure", async () => {
  const cdp = fakeCdp({
    "Page.navigate": { errorText: "net::ERR_NO_BUFFER_SPACE" },
    "Performance.getMetrics": { metrics: [] },
  });

  await assert.rejects(
    navigateReadme(cdp, "https://github.com/example/repo", async () => {}, async () => 123,
      () => 456, async () => {}),
    { message: "Edge navigation failed: net::ERR_NO_BUFFER_SPACE" });
  assert.equal(cdp.calls.filter(call => call.method === "Page.navigate").length, 2);
});
