import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import os from "node:os";
import path from "node:path";

const options = parseArguments(process.argv.slice(2));
const outputDirectory = path.resolve(required("out"));
const repositoryUrl = required("url");
const readmeSha = required("readme-sha");
const edgePath = options.edge || findDefaultEdge();
const viewportWidth = readPositiveInteger("width", 1000);
const viewportHeight = readPositiveInteger("height", 700);
const maximumTiles = readPositiveInteger("max-tiles", 512);
const profileDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-readme-edge-"));
await mkdir(outputDirectory, { recursive: true });

let edge;
let cdp;
const wall = performance.now();
class ReadmeNotRendered extends Error {}
try {
  edge = spawn(edgePath, [
    "--headless=new",
    "--no-first-run",
    "--no-default-browser-check",
    "--disable-background-networking",
    "--disable-component-update",
    "--disable-sync",
    "--metrics-recording-only",
    "--remote-debugging-port=0",
    `--user-data-dir=${profileDirectory}`,
    "about:blank",
  ], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });

  let edgeError = "";
  edge.stderr.setEncoding("utf8");
  edge.stderr.on("data", chunk => { edgeError = (edgeError + chunk).slice(-8192); });

  const portFile = path.join(profileDirectory, "DevToolsActivePort");
  const port = Number((await waitForFile(portFile, edge, edgeError)).split(/\r?\n/, 1)[0]);
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const pageTarget = targets.find(target => target.type === "page");
  if (!pageTarget?.webSocketDebuggerUrl) {
    throw new Error("Edge did not expose a debuggable page target.");
  }

  cdp = await connectCdp(pageTarget.webSocketDebuggerUrl);
  await Promise.all([
    cdp.send("Page.enable"),
    cdp.send("Runtime.enable"),
    cdp.send("Performance.enable"),
    cdp.send("Network.enable"),
  ]);
  await cdp.send("Emulation.setDeviceMetricsOverride", {
    width: viewportWidth,
    height: viewportHeight,
    deviceScaleFactor: 1,
    mobile: false,
  });
  await cdp.send("Emulation.setEmulatedMedia", {
    media: "screen",
    features: [{ name: "prefers-color-scheme", value: "light" }],
  });

  const loadEvent = cdp.once("Page.loadEventFired", 45_000);
  const navigationStarted = performance.now();
  const navigation = await cdp.send("Page.navigate", { url: repositoryUrl });
  if (navigation.errorText) {
    throw new Error(`Edge navigation failed: ${navigation.errorText}`);
  }
  await loadEvent;
  await waitForExpression(cdp, `document.readyState === "complete"`, 20_000);
  const readmeRendered = await waitForOptionalExpression(
    cdp,
    `Boolean(document.querySelector("#readme article.markdown-body, article.markdown-body"))`,
    5_000);
  if (!readmeRendered) {
    const elapsed = performance.now() - navigationStarted;
    const reportPath = path.join(outputDirectory, "browser.json");
    await writeFile(reportPath, JSON.stringify({
      schemaVersion: 2,
      repositoryUrl,
      readmeSha,
      readmeRendered: false,
      capturedAtUtc: new Date().toISOString(),
      viewport: { width: viewportWidth, height: viewportHeight, deviceScaleFactor: 1 },
      timing: { firstReadmeMs: elapsed, settledReadmeMs: elapsed, fullCaptureMs: elapsed, wallMs: performance.now() - wall },
      semantic: { text: "", headings: [], links: [], images: [], unavailableImages: 0, tables: 0, codeBlocks: 0, taskCheckboxes: 0, details: 0 },
      tiles: [],
    }, null, 2));
    process.stdout.write(JSON.stringify({ ok: true, readmeRendered: false, report: reportPath }) + "\n");
    throw new ReadmeNotRendered();
  }
  const firstReadmeMs = performance.now() - navigationStarted;

  await evaluate(cdp, `new Promise(async resolve => {
    const article = document.querySelector("#readme article.markdown-body, article.markdown-body");
    const top = article.getBoundingClientRect().top + scrollY;
    const bottom = top + article.getBoundingClientRect().height;
    const step = Math.max(240, Math.floor(innerHeight * 0.72));
    for (let y = top; y < bottom; y += step) {
      scrollTo(0, y);
      await new Promise(done => setTimeout(done, 70));
    }
    scrollTo(0, top);
    await new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done)));
    resolve(true);
  })`, true);

  await evaluate(cdp, `new Promise(resolve => {
    const article = document.querySelector("#readme article.markdown-body, article.markdown-body");
    const pending = [...article.querySelectorAll("img")].filter(image => !image.complete);
    if (pending.length === 0) { resolve(true); return; }
    let remaining = pending.length;
    const done = () => { if (--remaining === 0) resolve(true); };
    for (const image of pending) {
      image.addEventListener("load", done, { once: true });
      image.addEventListener("error", done, { once: true });
    }
    setTimeout(() => resolve(false), 15000);
  })`, true);

  const semantic = await evaluate(cdp, `(() => {
    const article = document.querySelector("#readme article.markdown-body, article.markdown-body");
    const rect = article.getBoundingClientRect();
    const clean = value => (value || "").replace(/\\s+/gu, " ").trim();
    const isRendered = node => {
      // Chromium can retain layout rectangles for descendants hidden by a
      // closed <details>. Match what a user can actually see, including links
      // inside the visible summary, rather than counting clipped descendants.
      const containingDetails = node.matches("details")
        ? node.parentElement?.closest("details")
        : node.closest("details");
      for (let details = containingDetails; details; details = details.parentElement?.closest("details")) {
        const summary = details.querySelector(":scope > summary");
        if (!details.open && !summary?.contains(node)) return false;
      }
      return [...node.getClientRects()]
        .some(bounds => bounds.width > 0 && bounds.height > 0);
    };
    const isImageSelfLink = node => {
      // GitHub automatically wraps otherwise unlinked README images in a
      // lightbox anchor whose target is that same rendered image. This is
      // repository-page chrome, not an authored Markdown link, so exclude it
      // from parity counts while retaining authored linked images whose target
      // leads somewhere else.
      if (clean(node.innerText)) return false;
      if (node.children.length !== 1 ||
          !["IMG", "PICTURE"].includes(node.children[0].tagName)) return false;
      const image = node.children[0].tagName === "IMG"
        ? node.children[0]
        : node.children[0].querySelector("img");
      if (!image) return false;
      const normalized = value => {
        try {
          const url = new URL(value, location.href);
          url.hash = "";
          return url.href;
        } catch {
          return "";
        }
      };
      const href = normalized(node.href);
      return href && [image.currentSrc, image.src]
        .map(normalized)
        .some(source => source === href);
    };
    const images = [...article.querySelectorAll("img")]
      .filter(isRendered)
      .map(image => {
        const bounds = image.getBoundingClientRect();
        return {
          alt: image.getAttribute("alt") || "",
          source: image.getAttribute("src") || "",
          currentSource: image.currentSrc || "",
          complete: image.complete,
          naturalWidth: image.naturalWidth,
          naturalHeight: image.naturalHeight,
          renderedWidth: bounds.width,
          renderedHeight: bounds.height,
        };
      })
      // GitHub can retain failed or inactive <picture> candidates with no
      // layout box. They are not part of the rendered README and therefore
      // must not create a false "unavailable" result or image-count mismatch.
      .filter(image => image.renderedWidth > 0 && image.renderedHeight > 0)
      // GitHub uses empty-src spacer <img> elements in a few READMEs. They have
      // layout boxes but no image resource, so they are neither a rendered image
      // nor an unavailable resource JitHub could be expected to reproduce.
      .filter(image => image.source || image.currentSource);
    // JitHub's UIA TextPattern represents an atomic image by its accessible
    // alt text. innerText intentionally omits image alternatives, so append the
    // rendered images' alt values to compare equivalent accessible documents.
    const accessibleText = clean([
      article.innerText,
      ...images.map(image => image.alt).filter(Boolean),
    ].join(" "));
    return {
      finalUrl: location.href,
      title: document.title,
      text: accessibleText,
      documentX: rect.left + scrollX,
      documentY: rect.top + scrollY,
      width: rect.width,
      height: rect.height,
      headings: [...article.querySelectorAll("h1,h2,h3,h4,h5,h6")]
        .filter(isRendered)
        .map(node => ({
          level: Number(node.tagName.slice(1)), text: clean(node.innerText),
        })),
      links: [...article.querySelectorAll("a[href]")]
        .filter(isRendered)
        .filter(node => !isImageSelfLink(node))
        .filter(node => {
          const text = clean(node.innerText);
          return text || !node.href.includes("#");
        })
        .map(node => ({
        text: clean(node.innerText), href: node.href,
      })),
      images,
      unavailableImages: images.filter(image => !image.complete || image.naturalWidth <= 0).length,
      tables: [...article.querySelectorAll("table")].filter(isRendered).length,
      codeBlocks: [...article.querySelectorAll("pre")].filter(isRendered).length,
      taskCheckboxes: [...article.querySelectorAll('input[type="checkbox"]')]
        .filter(isRendered).length,
      details: [...article.querySelectorAll("details")].filter(isRendered).length,
    };
  })()`);
  const settledReadmeMs = performance.now() - navigationStarted;
  // Capture the page's work metrics before screenshotting. Full-document CDP
  // captures can be expensive and are audit overhead, not GitHub rendering
  // work; including them made the native/Edge CPU comparison meaningless.
  const settledMetrics = await cdp.send("Performance.getMetrics");
  const settledMetricMap = Object.fromEntries(
    settledMetrics.metrics.map(metric => [metric.name, metric.value]));

  if (!(semantic.width > 0) || !(semantic.height > 0)) {
    throw new Error(`GitHub README has invalid bounds ${semantic.width}x${semantic.height}.`);
  }

  // Larger document-space tiles avoid asking Edge to re-raster an enormous
  // article once per 700px viewport while still keeping artifact images easy
  // to inspect and compare.
  const tileHeight = Math.min(Math.max(viewportHeight, 8192), Math.ceil(semantic.height));
  const tileCount = Math.max(1, Math.ceil(semantic.height / tileHeight));
  if (tileCount > maximumTiles) {
    throw new Error(`README needs ${tileCount} browser tiles, above the ${maximumTiles} safety ceiling.`);
  }

  const tiles = [];
  for (let index = 0; index < tileCount; index++) {
    const relativeY = Math.min(index * tileHeight, Math.max(0, semantic.height - tileHeight));
    const height = Math.min(tileHeight, semantic.height - relativeY);
    const capture = await cdp.send("Page.captureScreenshot", {
      format: "png",
      fromSurface: true,
      captureBeyondViewport: true,
      clip: {
        x: semantic.documentX,
        y: semantic.documentY + relativeY,
        width: semantic.width,
        height,
        scale: 1,
      },
    });
    const file = `tile-${String(index).padStart(4, "0")}.png`;
    await writeFile(path.join(outputDirectory, file), Buffer.from(capture.data, "base64"));
    tiles.push({ index, relativeY, width: semantic.width, height, file });
  }

  const navigationTiming = await evaluate(cdp, `(() => {
    const entry = performance.getEntriesByType("navigation")[0];
    return entry ? {
      responseEndMs: entry.responseEnd,
      domContentLoadedMs: entry.domContentLoadedEventEnd,
      loadMs: entry.loadEventEnd,
      transferBytes: entry.transferSize,
      decodedBytes: entry.decodedBodySize,
    } : null;
  })()`);
  const fullCaptureMetrics = await cdp.send("Performance.getMetrics");
  const fullCaptureMetricMap = Object.fromEntries(
    fullCaptureMetrics.metrics.map(metric => [metric.name, metric.value]));
  const report = {
    schemaVersion: 2,
    repositoryUrl,
    readmeSha,
    readmeRendered: true,
    capturedAtUtc: new Date().toISOString(),
    viewport: { width: viewportWidth, height: viewportHeight, deviceScaleFactor: 1 },
    timing: {
      firstReadmeMs,
      settledReadmeMs,
      fullCaptureMs: performance.now() - navigationStarted,
      wallMs: performance.now() - wall,
      navigation: navigationTiming,
      taskDurationMs: (settledMetricMap.TaskDuration || 0) * 1000,
      scriptDurationMs: (settledMetricMap.ScriptDuration || 0) * 1000,
      layoutDurationMs: (settledMetricMap.LayoutDuration || 0) * 1000,
      recalcStyleDurationMs: (settledMetricMap.RecalcStyleDuration || 0) * 1000,
      layoutCount: settledMetricMap.LayoutCount || 0,
      recalcStyleCount: settledMetricMap.RecalcStyleCount || 0,
      domNodes: settledMetricMap.Nodes || 0,
      documents: settledMetricMap.Documents || 0,
      jsHeapUsedBytes: settledMetricMap.JSHeapUsedSize || 0,
      fullCaptureTaskDurationMs: (fullCaptureMetricMap.TaskDuration || 0) * 1000,
    },
    semantic,
    tiles,
  };
  await writeFile(path.join(outputDirectory, "browser.json"), JSON.stringify(report, null, 2));
  process.stdout.write(JSON.stringify({ ok: true, report: path.join(outputDirectory, "browser.json") }) + "\n");
} catch (error) {
  if (error instanceof ReadmeNotRendered) {
    // A repository can have a README file that GitHub intentionally presents
    // as source (for example, an extensionless README). The source-view parity
    // path in the native audit handles this valid outcome.
  } else {
  const failure = { ok: false, repositoryUrl, error: error?.stack || String(error) };
  await writeFile(path.join(outputDirectory, "browser-failure.json"), JSON.stringify(failure, null, 2));
  process.stderr.write(failure.error + "\n");
  process.exitCode = 1;
  }
} finally {
  try { await cdp?.send("Browser.close"); } catch {}
  try { cdp?.close(); } catch {}
  if (edge && edge.exitCode === null) {
    await Promise.race([
      new Promise(resolve => edge.once("exit", resolve)),
      delay(1500),
    ]);
  }
  if (edge && edge.exitCode === null) {
    edge.kill();
    await Promise.race([
      new Promise(resolve => edge.once("exit", resolve)),
      delay(1500),
    ]);
  }
  for (let attempt = 0; attempt < 5; attempt++) {
    try {
      await rm(profileDirectory, { recursive: true, force: true });
      break;
    } catch (error) {
      if (attempt === 4) process.stderr.write(`warning: could not remove Edge profile: ${error.message}\n`);
      await delay(200 * (attempt + 1));
    }
  }
}

function parseArguments(args) {
  const result = {};
  for (const argument of args) {
    const match = /^--([^=]+)=(.*)$/u.exec(argument);
    if (match) result[match[1]] = match[2];
  }
  return result;
}

function required(name) {
  const value = options[name];
  if (!value) throw new Error(`Missing required --${name}=... argument.`);
  return value;
}

function readPositiveInteger(name, fallback) {
  const value = options[name] === undefined ? fallback : Number(options[name]);
  if (!Number.isInteger(value) || value <= 0) throw new Error(`--${name} must be a positive integer.`);
  return value;
}

function findDefaultEdge() {
  const candidates = [
    path.join(process.env["ProgramFiles(x86)"] || "", "Microsoft", "Edge", "Application", "msedge.exe"),
    path.join(process.env.ProgramFiles || "", "Microsoft", "Edge", "Application", "msedge.exe"),
  ];
  const candidate = candidates.find(value => value && path.isAbsolute(value) && existsSync(value));
  if (!candidate) throw new Error("Microsoft Edge was not found; pass --edge=...");
  return candidate;
}

async function waitForFile(file, processHandle, recentError) {
  const deadline = Date.now() + 20_000;
  while (Date.now() < deadline) {
    if (processHandle.exitCode !== null) {
      throw new Error(`Edge exited before DevTools became ready (${processHandle.exitCode}). ${recentError}`);
    }
    try { return await readFile(file, "utf8"); } catch {}
    await delay(50);
  }
  throw new Error(`Timed out waiting for Edge DevTools at ${file}. ${recentError}`);
}

async function connectCdp(url) {
  const socket = new WebSocket(url);
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("Timed out connecting to Edge DevTools.")), 10_000);
    socket.addEventListener("open", () => { clearTimeout(timer); resolve(); }, { once: true });
    socket.addEventListener("error", event => { clearTimeout(timer); reject(event.error || new Error("DevTools WebSocket failed.")); }, { once: true });
  });
  let sequence = 0;
  const pending = new Map();
  const eventWaiters = new Map();
  socket.addEventListener("message", event => {
    const message = JSON.parse(event.data);
    if (message.id) {
      const waiter = pending.get(message.id);
      if (!waiter) return;
      pending.delete(message.id);
      message.error ? waiter.reject(new Error(message.error.message)) : waiter.resolve(message.result || {});
      return;
    }
    const waiters = eventWaiters.get(message.method);
    if (!waiters?.length) return;
    eventWaiters.delete(message.method);
    for (const waiter of waiters) waiter.resolve(message.params || {});
  });
  return {
    send(method, params = {}) {
      const id = ++sequence;
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        socket.send(JSON.stringify({ id, method, params }));
      });
    },
    once(method, timeoutMs) {
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error(`Timed out waiting for ${method}.`)), timeoutMs);
        const waiter = { resolve: value => { clearTimeout(timer); resolve(value); }, reject };
        eventWaiters.set(method, [...(eventWaiters.get(method) || []), waiter]);
      });
    },
    close() { socket.close(); },
  };
}

async function evaluate(cdpClient, expression, awaitPromise = false) {
  const result = await cdpClient.send("Runtime.evaluate", {
    expression,
    awaitPromise,
    returnByValue: true,
    userGesture: false,
  });
  if (result.exceptionDetails) {
    throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
  }
  return result.result?.value;
}

async function waitForExpression(cdpClient, expression, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await evaluate(cdpClient, expression)) return;
    await delay(100);
  }
  throw new Error(`Timed out waiting for browser condition: ${expression}`);
}

async function waitForOptionalExpression(cdpClient, expression, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await evaluate(cdpClient, expression)) return true;
    await delay(100);
  }
  return false;
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
