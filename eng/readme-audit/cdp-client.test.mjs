import assert from "node:assert/strict";
import test from "node:test";
import { connectCdp } from "./cdp-client.mjs";

class FakeSocket {
  static latest;
  static autoOpen = true;

  constructor() {
    FakeSocket.latest = this;
    this.listeners = new Map();
    this.sent = [];
    this.closeCount = 0;
    if (FakeSocket.autoOpen) queueMicrotask(() => this.emit("open"));
  }

  addEventListener(name, listener, options = {}) {
    const items = this.listeners.get(name) || [];
    items.push({ listener, once: options.once === true });
    this.listeners.set(name, items);
  }

  emit(name, data = {}) {
    const items = this.listeners.get(name) || [];
    this.listeners.set(name, items.filter(item => !item.once));
    for (const item of items) item.listener(data);
  }

  send(payload) { this.sent.push(JSON.parse(payload)); }

  close() {
    this.closeCount++;
    this.emit("close");
  }
}

test("DevTools replies and events resolve with the original protocol IDs", async () => {
  FakeSocket.autoOpen = true;
  const cdp = await connectCdp("ws://test.invalid", { WebSocketClass: FakeSocket });
  const socket = FakeSocket.latest;
  const events = [];
  const remove = cdp.on("Page.loadEventFired", value => events.push(value));
  const event = cdp.once("Page.loadEventFired", 100);
  const command = cdp.send("Page.enable", { test: 1 });
  assert.deepEqual(socket.sent, [{ id: 1, method: "Page.enable", params: { test: 1 } }]);
  socket.emit("message", { data: JSON.stringify({ id: 1, result: { enabled: true } }) });
  socket.emit("message", { data: JSON.stringify({ method: "Page.loadEventFired", params: { timestamp: 2 } }) });
  assert.deepEqual(await command, { enabled: true });
  assert.deepEqual(await event, { timestamp: 2 });
  assert.deepEqual(events, [{ timestamp: 2 }]);
  remove();
  cdp.close();
});

test("DevTools connection timeout closes the unresponsive socket", async () => {
  FakeSocket.autoOpen = false;
  await assert.rejects(
    connectCdp("ws://test.invalid", { WebSocketClass: FakeSocket, connectionTimeoutMs: 10 }),
    /Timed out connecting/u);
  assert.equal(FakeSocket.latest.closeCount, 1);
  FakeSocket.autoOpen = true;
});

test("DevTools connection error reports the transport failure", async () => {
  FakeSocket.autoOpen = false;
  const connection = connectCdp("ws://test.invalid", { WebSocketClass: FakeSocket });
  FakeSocket.latest.emit("error");
  await assert.rejects(connection, /WebSocket failed/u);
  assert.equal(FakeSocket.latest.closeCount, 1);
  FakeSocket.autoOpen = true;
});

test("DevTools command timeout fails all in-flight work instead of hanging", async () => {
  const cdp = await connectCdp("ws://test.invalid", {
    WebSocketClass: FakeSocket,
    commandTimeoutMs: 10,
  });
  const first = cdp.send("Page.captureScreenshot");
  const second = cdp.send("Runtime.evaluate");
  const event = cdp.once("Page.loadEventFired", 100);
  await assert.rejects(first, /Timed out waiting for Edge DevTools command/u);
  await assert.rejects(second, /Timed out waiting for Edge DevTools command/u);
  await assert.rejects(event, /Timed out waiting for Edge DevTools command/u);
  await assert.rejects(cdp.send("Page.enable"), /WebSocket is closed/u);
  assert.equal(FakeSocket.latest.closeCount, 1);
});

test("DevTools socket loss and malformed replies reject pending commands", async () => {
  const cdp = await connectCdp("ws://test.invalid", { WebSocketClass: FakeSocket });
  const command = cdp.send("Page.enable");
  FakeSocket.latest.emit("message", { data: "not-json" });
  await assert.rejects(command, /malformed JSON/u);
  assert.equal(FakeSocket.latest.closeCount, 1);

  const next = await connectCdp("ws://test.invalid", { WebSocketClass: FakeSocket });
  const pending = next.send("Runtime.evaluate");
  FakeSocket.latest.emit("close");
  await assert.rejects(pending, /closed before the audit completed/u);
});

test("DevTools event waiters time out without closing a healthy channel", async () => {
  const cdp = await connectCdp("ws://test.invalid", { WebSocketClass: FakeSocket });
  await assert.rejects(cdp.once("Page.never", 10), /Timed out waiting for Edge DevTools event/u);
  const command = cdp.send("Page.enable");
  const id = FakeSocket.latest.sent.at(-1).id;
  FakeSocket.latest.emit("message", { data: JSON.stringify({ id, result: {} }) });
  assert.deepEqual(await command, {});
  cdp.close();
});
