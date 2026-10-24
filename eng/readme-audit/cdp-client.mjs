const DEFAULT_CONNECTION_TIMEOUT_MS = 10_000;
const DEFAULT_COMMAND_TIMEOUT_MS = 60_000;

/**
 * A bounded DevTools transport. A lost Edge socket or unanswered command must
 * fail the audit, not leave a top-500 shard waiting indefinitely. Timeouts do
 * not retry or convert a failed comparison into a pass.
 */
export async function connectCdp(url, {
  WebSocketClass = WebSocket,
  connectionTimeoutMs = DEFAULT_CONNECTION_TIMEOUT_MS,
  commandTimeoutMs = DEFAULT_COMMAND_TIMEOUT_MS,
} = {}) {
  requireTimeout(connectionTimeoutMs, "connectionTimeoutMs");
  requireTimeout(commandTimeoutMs, "commandTimeoutMs");
  const socket = new WebSocketClass(url);
  const pending = new Map();
  const eventWaiters = new Map();
  const eventListeners = new Map();
  let sequence = 0;
  let closed = false;

  function rejectOutstanding(error) {
    for (const waiter of pending.values()) {
      clearTimeout(waiter.timer);
      waiter.reject(error);
    }
    pending.clear();
    for (const waiters of eventWaiters.values()) {
      for (const waiter of waiters) {
        clearTimeout(waiter.timer);
        waiter.reject(error);
      }
    }
    eventWaiters.clear();
  }

  function fail(error) {
    if (closed) return;
    closed = true;
    rejectOutstanding(error);
    eventListeners.clear();
    try { socket.close(); } catch {}
  }

  const connected = new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      const error = new Error("Timed out connecting to Edge DevTools.");
      reject(error);
      fail(error);
    }, connectionTimeoutMs);
    socket.addEventListener("open", () => {
      clearTimeout(timer);
      if (closed) return;
      resolve();
    }, { once: true });
    socket.addEventListener("error", () => {
      clearTimeout(timer);
      const error = new Error("Edge DevTools WebSocket failed.");
      reject(error);
      fail(error);
    });
    socket.addEventListener("close", () => {
      clearTimeout(timer);
      const error = new Error("Edge DevTools WebSocket closed before the audit completed.");
      fail(error);
      reject(error);
    });
  });

  socket.addEventListener("message", event => {
    if (closed) return;
    let message;
    try {
      message = JSON.parse(event.data);
      if (!message || typeof message !== "object") throw new Error();
    } catch {
      fail(new Error("Edge DevTools returned malformed JSON."));
      return;
    }
    if (message.id !== undefined) {
      const waiter = pending.get(message.id);
      if (!waiter) return;
      pending.delete(message.id);
      clearTimeout(waiter.timer);
      message.error
        ? waiter.reject(new Error(message.error.message || "Edge DevTools command failed."))
        : waiter.resolve(message.result || {});
      return;
    }
    for (const listener of eventListeners.get(message.method) || []) {
      try { listener(message.params || {}); } catch {}
    }
    const waiters = eventWaiters.get(message.method);
    if (!waiters?.size) return;
    eventWaiters.delete(message.method);
    for (const waiter of waiters) {
      clearTimeout(waiter.timer);
      waiter.resolve(message.params || {});
    }
  });

  await connected;
  return {
    send(method, params = {}) {
      if (closed) return Promise.reject(new Error("Edge DevTools WebSocket is closed."));
      const id = ++sequence;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          fail(new Error(`Timed out waiting for Edge DevTools command ${method}.`));
        }, commandTimeoutMs);
        pending.set(id, { resolve, reject, timer });
        try {
          socket.send(JSON.stringify({ id, method, params }));
        } catch {
          fail(new Error(`Unable to send Edge DevTools command ${method}.`));
        }
      });
    },
    once(method, timeoutMs = commandTimeoutMs) {
      requireTimeout(timeoutMs, "event timeout");
      if (closed) return Promise.reject(new Error("Edge DevTools WebSocket is closed."));
      return new Promise((resolve, reject) => {
        const waiter = { resolve, reject, timer: undefined };
        waiter.timer = setTimeout(() => {
          eventWaiters.get(method)?.delete(waiter);
          if (eventWaiters.get(method)?.size === 0) eventWaiters.delete(method);
          reject(new Error(`Timed out waiting for Edge DevTools event ${method}.`));
        }, timeoutMs);
        const waiters = eventWaiters.get(method) || new Set();
        waiters.add(waiter);
        eventWaiters.set(method, waiters);
      });
    },
    on(method, listener) {
      if (closed) throw new Error("Edge DevTools WebSocket is closed.");
      const listeners = eventListeners.get(method) || new Set();
      listeners.add(listener);
      eventListeners.set(method, listeners);
      return () => {
        listeners.delete(listener);
        if (listeners.size === 0) eventListeners.delete(method);
      };
    },
    close() { fail(new Error("Edge DevTools connection was closed by the audit.")); },
  };
}

function requireTimeout(value, name) {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`${name} must be a positive safe integer.`);
  }
}
