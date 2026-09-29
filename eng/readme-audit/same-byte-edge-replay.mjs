import { writeFile } from "node:fs/promises";
import path from "node:path";
import { sha256 } from "./same-byte-corpus.mjs";
import {
  MAX_SAME_BYTE_TRAVERSAL_VIEWPORTS,
  SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO,
} from "./same-byte-traversal.mjs";

const MAX_REPLAY_STEPS = MAX_SAME_BYTE_TRAVERSAL_VIEWPORTS;
const MAX_IMAGE_WAIT_MS = 15_000;

/**
 * Replays the captured, sanitized GitHub article in the already-running Edge
 * instance. The browser receives only capture HTML plus content-addressed
 * loopback image routes; all non-loopback requests are denied and counted.
 * Timing is measured inside the replay page, so live GitHub navigation/CDN
 * work cannot enter the same-byte rendering denominator.
 */
export async function replaySameByteInEdge({
  cdp,
  replayServer,
  outputDirectory,
  viewport,
  maximumTiles,
}) {
  if (!cdp || !replayServer?.baseUrl || !replayServer.renderedHtmlBytes ||
      !Array.isArray(replayServer.imageRoutes) || !outputDirectory ||
      !Number.isSafeInteger(viewport?.width) || !Number.isSafeInteger(viewport?.height) ||
      !Number.isSafeInteger(maximumTiles) || maximumTiles <= 0) {
    throw new Error("Offline Edge replay requires a validated rendered corpus, viewport, and bounded output target.");
  }

  const origin = new URL(replayServer.baseUrl).origin;
  const blockedExternalUrls = new Set();
  let fetchFailure = "";
  const removeFetchListener = cdp.on("Fetch.requestPaused", event => {
    let requestUrl;
    try { requestUrl = new URL(event.request.url); } catch {}
    const isReplayRequest = requestUrl?.origin === origin &&
      (requestUrl.pathname === "/replay" ||
       requestUrl.pathname === "/favicon.ico" ||
       /^\/asset-by-content-sha256\/[0-9a-f]{64}$/u.test(requestUrl.pathname));
    if (!isReplayRequest) {
      blockedExternalUrls.add(event.request.url);
      void cdp.send("Fetch.failRequest", {
        requestId: event.requestId,
        errorReason: "BlockedByClient",
      }).catch(error => { fetchFailure ||= error.message || String(error); });
      return;
    }
    void cdp.send("Fetch.continueRequest", { requestId: event.requestId })
      .catch(error => { fetchFailure ||= error.message || String(error); });
  });

  try {
    // The live GitHub document may still have late image/analytics requests.
    // Clear that document before enabling request interception, otherwise an
    // unrelated in-flight request can be misattributed to the offline page.
    await cdp.send("Page.navigate", { url: "about:blank" });
    await waitForExpression(cdp,
      "location.href === 'about:blank' && document.readyState === 'complete'", 10_000);
    await Promise.all([
      cdp.send("Network.setCacheDisabled", { cacheDisabled: true }),
      cdp.send("Network.setBypassServiceWorker", { bypass: true }),
      cdp.send("Fetch.enable", { patterns: [{ urlPattern: "*", requestStage: "Request" }] }),
    ]);
    await cdp.send("Page.navigate", { url: `${origin}/replay` });
    await waitForExpression(cdp, "document.readyState === 'complete'", 10_000);

    const global = await cdp.send("Runtime.evaluate", {
      expression: "window",
      returnByValue: false,
    });
    if (!global.result?.objectId) throw new Error("Edge did not expose the offline replay context.");
    const replayResult = await cdp.send("Runtime.callFunctionOn", {
      objectId: global.result.objectId,
      functionDeclaration: replayFunctionDeclaration(),
      arguments: [
        { value: replayServer.renderedHtmlBytes.toString("utf8") },
        { value: replayServer.imageRoutes },
        { value: replayServer.baseUrl },
        { value: MAX_IMAGE_WAIT_MS },
        { value: MAX_REPLAY_STEPS },
        { value: SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO },
      ],
      awaitPromise: true,
      returnByValue: true,
      userGesture: false,
    });
    if (replayResult.exceptionDetails) {
      throw new Error(replayResult.exceptionDetails.exception?.description || replayResult.exceptionDetails.text);
    }
    const replay = replayResult.result?.value;
    if (!replay || replay.ok !== true) {
      throw new Error(replay?.error || "Offline Edge replay returned no successful result.");
    }

    await new Promise(resolve => setTimeout(resolve, 100));
    const blockedResourceSet = new Set(blockedExternalUrls);
    for (const blockedUri of replay.blockedExternalUris || []) {
      if (/^(?:https?:|file:|ftp:|ws:|wss:|blob:)/iu.test(blockedUri) &&
          !blockedUri.startsWith(`${origin}/`)) {
        blockedResourceSet.add(blockedUri);
      }
    }
    if (fetchFailure) throw new Error(`Offline Edge request isolation failed: ${fetchFailure}`);
    if (blockedResourceSet.size !== 0) {
      const blockedEvidence = [...blockedResourceSet].map(value => {
        let originEvidence = "opaque";
        try {
          const parsed = new URL(value);
          originEvidence = `scheme=${parsed.protocol} hostHash=${sha256(Buffer.from(parsed.hostname, "utf8"))}`;
        } catch {}
        const source = blockedExternalUrls.has(value) ? "fetch" : "csp";
        return `${source} ${originEvidence} urlHash=${sha256(Buffer.from(value, "utf8"))}`;
      });
      throw new Error(`Offline Edge replay attempted ${blockedResourceSet.size} external resource request(s): ${blockedEvidence.join(", ")}.`);
    }

    const tileHeight = Math.min(Math.max(viewport.height, 8192), Math.ceil(replay.height));
    const tileCount = Math.max(1, Math.ceil(replay.height / tileHeight));
    if (tileCount > maximumTiles) {
      throw new Error(`Offline replay needs ${tileCount} browser tiles, above the ${maximumTiles} safety ceiling.`);
    }
    const tiles = [];
    for (let index = 0; index < tileCount; index++) {
      const relativeY = Math.min(index * tileHeight, Math.max(0, replay.height - tileHeight));
      const height = Math.min(tileHeight, replay.height - relativeY);
      const capture = await cdp.send("Page.captureScreenshot", {
        format: "png",
        fromSurface: true,
        captureBeyondViewport: true,
        clip: { x: 0, y: relativeY, width: replay.width, height, scale: 1 },
      });
      const file = `same-byte-tile-${String(index).padStart(4, "0")}.png`;
      await writeFile(path.join(outputDirectory, file), Buffer.from(capture.data, "base64"));
      tiles.push({ index, relativeY, width: replay.width, height, file });
    }

    const servedUrlHashes = replayServer.servedUrlSha256s;
    const expectedUrlHashes = replayServer.expectedUrlHashes;
    if (replayServer.misses !== 0 ||
        replayServer.servedImageRoutes !== replayServer.imageRoutes.filter(route => route.dataUri !== true).length ||
        expectedUrlHashes.length !== servedUrlHashes.length ||
        expectedUrlHashes.some((value, index) => value !== servedUrlHashes[index])) {
      throw new Error("Offline Edge replay did not serve the complete captured image URL map exactly.");
    }
    if (replay.loadedImageCount !== replayServer.expectedVisibleImageCount) {
      throw new Error("Offline Edge replay did not decode every captured visible image.");
    }

    return {
      schemaVersion: 1,
      status: "passed",
      readmeGitBlobSha1: replayServer.readmeGitBlobSha1,
      readmeSha256: replayServer.readmeSha256,
      renderedHtmlSha256: replayServer.renderedHtmlSha256,
      assetUrlMapSha256: replayServer.assetUrlMapSha256,
      viewport: { ...viewport, deviceScaleFactor: 1 },
      assets: {
        expectedVisibleImageCount: replayServer.expectedVisibleImageCount,
        firstViewportRealizedImageCount: replay.firstViewportRealizedImageCount,
        distinctExpectedUrlHashes: expectedUrlHashes.length,
        distinctServedUrlHashes: servedUrlHashes.length,
        replayMissCount: replayServer.misses,
        blockedExternalRequestCount: blockedResourceSet.size,
        dataImageCount: replayServer.imageRoutes.filter(route => route.dataUri === true).length,
      },
      timing: {
        firstViewportPaintMs: replay.timing.firstViewportPaintMs,
        firstViewportImagesReadyMs: replay.timing.firstViewportImagesReadyMs,
        fullTraversalMs: replay.timing.fullTraversalMs,
        viewportStepRatio: replay.timing.viewportStepRatio,
        traversalViewportCount: replay.timing.traversalViewportCount,
        firstViewportBoundary: "captured HTML parsed and inserted; two requestAnimationFrames elapsed",
        firstViewportImagesReadyBoundary: "same start through successful decode of every visible image",
        fullTraversalBoundary: "same start through bottom traversal, visible-image decode at each step, and final paint",
      },
      tiles,
    };
  } finally {
    removeFetchListener?.();
    try { await cdp.send("Fetch.disable"); } catch {}
    try { await cdp.send("Network.setCacheDisabled", { cacheDisabled: false }); } catch {}
  }
}

export function replayFunctionDeclaration() {
  return `async function(html, imageRoutes, baseUrl, imageWaitMs, maximumSteps, viewportStepRatio) {
    const blockedExternalUris = new Set();
    const isExternalResource = value => /^(?:https?:|file:|ftp:|ws:|wss:|blob:)/iu.test(value || "");
    const onPolicyViolation = event => {
      if (isExternalResource(event.blockedURI)) blockedExternalUris.add(event.blockedURI);
    };
    addEventListener("securitypolicyviolation", onPolicyViolation);
    const started = performance.now();
    try {
      const parsed = new DOMParser().parseFromString(html, "text/html");
      if (parsed.querySelector("script,iframe,object,embed,base,link[rel~='stylesheet']")) {
        throw new Error("Captured article contains an active or externally styled element.");
      }
      if (parsed.querySelector("[src], [srcset], [poster], [background]")) {
        throw new Error("Captured article contains a resource URL outside the content-addressed image map.");
      }
      for (const style of parsed.querySelectorAll("[style],style")) {
        const value = style.tagName === "STYLE" ? style.textContent : style.getAttribute("style");
        const externalCss = /@import/iu.test(value || "") ||
          [...(value || "").matchAll(/url\\s*\\(\\s*(?:(["'])(.*?)\\1|([^)]+))\\s*\\)/giu)]
            .some(match => !(match[2] ?? match[3] ?? "").trim().startsWith("#"));
        if (externalCss) {
          throw new Error("Captured article contains a CSS resource reference outside the image map.");
        }
      }
      const article = parsed.body.firstElementChild;
      if (!article || article.tagName !== "ARTICLE") {
        throw new Error("Captured GitHub HTML does not contain exactly one article root.");
      }
      const routeByIndex = new Map(imageRoutes.map(route => [route.index, route]));
      const allImages = [...article.querySelectorAll("img")];
      const pendingImageSources = new Map();
      let loadedImageCount = 0;
      for (const image of allImages) {
        image.removeAttribute("srcset");
        image.removeAttribute("sizes");
        image.removeAttribute("src");
        image.loading = "lazy";
        const index = Number(image.getAttribute("data-jithub-image-index"));
        const route = routeByIndex.get(index);
        if (!route) {
          image.removeAttribute("src");
          image.removeAttribute("data-jithub-image-data");
          continue;
        }
        if (route.dataUri === true) {
          const dataUri = image.getAttribute("data-jithub-image-data") || "";
          if (!/^data:image\\/[a-z0-9.+-]+(?:;[^,]*)?,/iu.test(dataUri)) {
            throw new Error("A captured data image is missing its bounded data URI.");
          }
          pendingImageSources.set(image, dataUri);
        } else {
          if (!/^[0-9a-f]{64}$/u.test(route.sha256 || "")) {
            throw new Error("A captured image route has an invalid content digest.");
          }
          pendingImageSources.set(
            image,
            baseUrl + "/asset-by-content-sha256/" + route.sha256 + "?index=" + index);
        }
        image.removeAttribute("data-jithub-image-data");
      }
      const allImageCount = allImages.length;
      document.documentElement.lang = article.getAttribute("lang") || "en";
      document.body.replaceChildren(article);
      document.body.style.margin = "0";
      const nextFrame = () => new Promise(resolve => requestAnimationFrame(() => resolve()));
      const imageRealizationOverscanPx = 800;
      const realizeImagesNearViewport = () => {
        const viewportTop = scrollY;
        const bandTop = viewportTop - imageRealizationOverscanPx;
        const bandBottom = viewportTop + innerHeight + imageRealizationOverscanPx;
        for (const [image, source] of pendingImageSources) {
          const rect = image.getBoundingClientRect();
          const imageTop = rect.top + viewportTop;
          const imageBottom = rect.bottom + viewportTop;
          if (imageBottom < bandTop || imageTop > bandBottom) continue;
          image.loading = "eager";
          image.src = source;
          pendingImageSources.delete(image);
        }
      };
      const waitImageDecode = async image => {
        const ready = await new Promise(resolve => {
          let settled = false;
          const finish = result => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            image.removeEventListener("load", onLoad);
            image.removeEventListener("error", onError);
            resolve(result);
          };
          const onLoad = () => finish(image.naturalWidth > 0 && image.naturalHeight > 0);
          const onError = () => finish(false);
          const timer = setTimeout(() => finish(false), imageWaitMs);
          image.addEventListener("load", onLoad, { once: true });
          image.addEventListener("error", onError, { once: true });
          if (image.complete) finish(image.naturalWidth > 0 && image.naturalHeight > 0);
        });
        if (!ready) return false;
        try {
          await image.decode();
          return image.complete && image.naturalWidth > 0 && image.naturalHeight > 0;
        } catch {
          return false;
        }
      };
      const visibleImages = () => [...document.images].filter(image => {
          const rect = image.getBoundingClientRect();
          return rect.width > 0 && rect.height > 0 && rect.bottom > 0 && rect.top < innerHeight &&
            rect.right > 0 && rect.left < innerWidth;
        });
      const waitVisibleImages = async () => {
        const visible = visibleImages();
        const ready = await Promise.all(visible.map(waitImageDecode));
        if (ready.some(value => !value)) throw new Error("A visible offline image did not decode successfully.");
        return visible.length;
      };
      const capturedImages = [...document.images].filter(image =>
        routeByIndex.has(Number(image.getAttribute("data-jithub-image-index"))));
      realizeImagesNearViewport();
      await nextFrame();
      await nextFrame();
      const firstViewportPaintMs = performance.now() - started;
      await waitVisibleImages();
      const firstViewportImagesReadyMs = performance.now() - started;
      const firstViewportRealizedImageCount = capturedImages.length - pendingImageSources.size;
      if (!Number.isFinite(viewportStepRatio) || viewportStepRatio <= 0 || viewportStepRatio >= 1) {
        throw new Error("Offline Edge traversal received an invalid viewport step ratio.");
      }
      const step = Math.max(1, innerHeight * viewportStepRatio);
      let traversalViewportCount = 0;
      let position = 0;
      while (true) {
        if (++traversalViewportCount > maximumSteps) {
          throw new Error("Offline Edge traversal exceeded its bounded viewport count.");
        }
        scrollTo(0, position);
        await nextFrame();
        realizeImagesNearViewport();
        await waitVisibleImages();
        await nextFrame();
        const actualTop = scrollY;
        const maxTop = Math.max(0, document.documentElement.scrollHeight - innerHeight);
        if (actualTop < maxTop) {
          const nextPosition = Math.min(maxTop, actualTop + step);
          if (nextPosition <= actualTop) {
            throw new Error("Offline Edge traversal could not advance while content remained below the viewport.");
          }
          position = nextPosition;
          continue;
        }

        // A lazy image can expand content above the current viewport. Decode
        // every captured image at the candidate bottom, then re-check the
        // extent and current top before declaring traversal complete.
        realizeImagesNearViewport();
        if (pendingImageSources.size !== 0) {
          throw new Error("Offline traversal reached its terminal viewport with captured images outside the realization band.");
        }
        const readyImages = await Promise.all(capturedImages.map(waitImageDecode));
        if (readyImages.some(value => !value)) {
          throw new Error("Offline traversal left a captured image unavailable.");
        }
        loadedImageCount = capturedImages.length;
        const heightBeforePaint = document.documentElement.scrollHeight;
        await nextFrame();
        await waitVisibleImages();
        await nextFrame();
        const heightAfterPaint = document.documentElement.scrollHeight;
        const confirmedTop = scrollY;
        const confirmedMaxTop = Math.max(0, heightAfterPaint - innerHeight);
        if (heightAfterPaint !== heightBeforePaint || confirmedTop < confirmedMaxTop) {
          position = confirmedTop;
          continue;
        }

        await nextFrame();
        await nextFrame();
        const finalHeight = document.documentElement.scrollHeight;
        const finalMaxTop = Math.max(0, finalHeight - innerHeight);
        if (finalHeight !== heightAfterPaint || scrollY < finalMaxTop) {
          position = scrollY;
          continue;
        }
        break;
      }
      const fullTraversalMs = performance.now() - started;
      const width = Math.ceil(Math.max(document.documentElement.scrollWidth, innerWidth));
      const height = Math.ceil(Math.max(document.documentElement.scrollHeight, innerHeight));
      if (!(width > 0) || !(height > 0) || width > 16_384 || height > 100_000_000) {
        throw new Error("Offline Edge article has invalid or over-budget dimensions.");
      }
      await new Promise(resolve => setTimeout(resolve, 50));
      return {
        ok: true,
        timing: {
          firstViewportPaintMs,
          firstViewportImagesReadyMs,
          fullTraversalMs,
          viewportStepRatio,
          traversalViewportCount,
        },
        width: Math.min(width, innerWidth),
        height,
        loadedImageCount,
        firstViewportRealizedImageCount,
        capturedImageElementCount: allImageCount,
        blockedExternalUris: [...blockedExternalUris],
      };
    } finally {
      removeEventListener("securitypolicyviolation", onPolicyViolation);
    }
  }`;
}

async function waitForExpression(cdp, expression, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const result = await cdp.send("Runtime.evaluate", {
        expression,
        returnByValue: true,
      });
      if (!result.exceptionDetails && result.result?.value) return;
    } catch {
      // Page.navigate briefly replaces the JavaScript execution context.
    }
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error("Timed out waiting for the loopback replay document.");
}
