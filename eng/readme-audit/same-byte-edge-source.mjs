import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { lstat, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { waitForDevToolsPort } from "./browser-launch.mjs";
import { connectCdp } from "./cdp-client.mjs";
import { stopBrowserProfileProcesses } from "./browser-process-lifetime.mjs";
import {
  createCapturedImageAltIdentities,
  createSameByteReplayServer,
  PINNED_GFM_PARSER,
  sha256,
} from "./same-byte-corpus.mjs";
import {
  MAX_SAME_BYTE_TRAVERSAL_VIEWPORTS,
  SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO,
} from "./same-byte-traversal.mjs";

const MAX_IMAGE_WAIT_MS = 15_000;
const MAX_REPLAY_STEPS = MAX_SAME_BYTE_TRAVERSAL_VIEWPORTS;
const DEFAULT_MAXIMUM_TILES = 512;
const MAX_INLINE_IMAGE_URI_BYTES = 16 * 1024 * 1024;
const MAX_INLINE_IMAGE_TOTAL_BYTES = 16 * 1024 * 1024;
const SOURCE_IMAGE_MIMES = new Set([
  "image/avif",
  "image/bmp",
  "image/gif",
  "image/jpeg",
  "image/png",
  "image/svg+xml",
  "image/webp",
  "image/x-icon",
]);
const SOURCE_REPLAY_STAGES = new Set([
  "initialization",
  "reset-document",
  "configure-edge",
  "load-replay-page",
  "execute-source-replay",
  "source-function-exception",
  "verify-request-isolation",
  "capture-tiles",
  "verify-captured-assets",
  "viewport",
  "manifest-response",
  "manifest-digest",
  "manifest-identity",
  "asset-map",
  "readme-response",
  "readme-digest",
  "parser-pin",
  "parse-markdown",
  "sanitize-markdown",
  "image-map",
  "inline-image-evidence",
  "first-viewport",
  "visible-image-decode",
  "native-initial-offset",
  "native-movement",
  "native-capture-position",
  "native-capture-step",
  "native-terminal-images",
  "native-terminal-profile-bottom",
  "native-terminal-source-bottom",
  "native-terminal-stability",
  "native-terminal-stability-profile",
  "native-terminal-stability-bottom",
  "native-terminal-confirmation",
  "source-tail-movement",
  "source-tail-settle",
  "source-tail-observation",
  "source-tail-terminal-images",
  "source-tail-terminal-confirmation",
  "overlap-traversal",
  "terminal-images",
  "terminal-confirmation",
  "rendered-dimensions",
  "semantic-summary",
]);
const SOURCE_REPLAY_ERROR_TYPES = new Set([
  "AbortError",
  "DOMException",
  "Error",
  "RangeError",
  "ReferenceError",
  "SyntaxError",
  "TimeoutError",
  "TypeError",
]);
const SOURCE_REPLAY_FAILURE_PREDICATES = new Set([
  "conflicting-image-aliases",
  "invalid-image-url",
  "invalid-inline-image-data",
  "invalid-image-dimension",
  "inline-image-byte-mismatch",
  "missing-captured-image-alias",
  "unmatched-inline-image-evidence",
  "responsive-image-candidates",
  "sanitized-image-count",
  "unsupported-image-url-scheme",
]);

/**
 * Runs the captured raw Markdown through the pinned GFM parser in Edge. The
 * loopback server validates all captured files before Edge can request them;
 * this function times only the client-side parse, DOM construction, visible
 * image readiness, and full traversal.
 */
export async function replaySourceBoundMarkdownInEdge({
  cdp,
  replayServer,
  outputDirectory,
  viewport,
  colorScheme = "light",
  semanticDigestKey,
  maximumTiles = DEFAULT_MAXIMUM_TILES,
  viewportStepRatio = SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO,
  nativeViewportProfile = null,
}) {
  viewport = { ...viewport, deviceScaleFactor: viewport?.deviceScaleFactor ?? 1 };
  validateReplayArguments({
    cdp, replayServer, outputDirectory, viewport, colorScheme, semanticDigestKey,
    maximumTiles, viewportStepRatio, nativeViewportProfile,
  });
  if (replayServer.manifest?.browserRender?.status !== "passed") {
    throw new Error("Source-bound Edge replay requires a captured GitHub-rendered article.");
  }
  const capturedInlineImageEvidence = readCapturedInlineImageEvidence(replayServer);
  const capturedImageAltIdentities = createCapturedImageAltIdentities(
    replayServer.renderedHtmlBytes,
    replayServer.manifest.imageRoutes,
  );

  const origin = new URL(replayServer.baseUrl).origin;
  const blockedExternalUrls = new Set();
  let fetchFailure = "";
  let failureStage = "initialization";
  const removeFetchListener = cdp.on("Fetch.requestPaused", event => {
    let requestUrl;
    try { requestUrl = new URL(event.request.url); } catch {}
    const isSourceReplayRequest = requestUrl?.origin === origin &&
      (requestUrl.pathname === "/source" ||
       requestUrl.pathname === "/marked-parser.js" ||
       requestUrl.pathname === "/manifest" ||
       requestUrl.pathname === "/readme" ||
       requestUrl.pathname === "/favicon.ico" ||
       /^\/asset-by-(?:url|content)-sha256\/[0-9a-f]{64}$/u.test(requestUrl.pathname));
    if (!isSourceReplayRequest) {
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
    failureStage = "reset-document";
    await cdp.send("Page.navigate", { url: "about:blank" });
    await waitForExpression(cdp,
      "location.href === 'about:blank' && document.readyState === 'complete'", 10_000);
    failureStage = "configure-edge";
    await Promise.all([
      cdp.send("Network.setCacheDisabled", { cacheDisabled: true }),
      cdp.send("Network.setBypassServiceWorker", { bypass: true }),
      cdp.send("Fetch.enable", { patterns: [{ urlPattern: "*", requestStage: "Request" }] }),
      cdp.send("Emulation.setDeviceMetricsOverride", {
        width: viewport.width,
        height: viewport.height,
        deviceScaleFactor: viewport.deviceScaleFactor,
        mobile: false,
      }),
      cdp.send("Emulation.setEmulatedMedia", {
        media: "screen",
        features: [{ name: "prefers-color-scheme", value: colorScheme }],
      }),
    ]);
    failureStage = "load-replay-page";
    await cdp.send("Page.navigate", { url: `${origin}/source` });
    await waitForExpression(cdp, "document.readyState === 'complete' && typeof marked === 'object'", 15_000);

    const global = await cdp.send("Runtime.evaluate", {
      expression: "window",
      returnByValue: false,
    });
    if (!global.result?.objectId) throw new Error("Edge did not expose the source-bound replay context.");
    failureStage = "execute-source-replay";
    const result = await cdp.send("Runtime.callFunctionOn", {
      objectId: global.result.objectId,
      functionDeclaration: sourceReplayFunctionDeclaration(),
      arguments: [
        { value: replayServer.baseUrl },
        { value: {
          readmeBaseUrl: githubReadmePageUrl(replayServer.manifest.repository),
          readmeGitBlobSha1: replayServer.readmeGitBlobSha1,
          readmeSha256: replayServer.readmeSha256,
          readmeByteSize: replayServer.readmeBytes.length,
          manifestSha256: replayServer.manifestSha256,
          assetUrlMapSha256: replayServer.assetUrlMapSha256,
          capturedInlineImageEvidence,
          capturedImageAltIdentities,
          capturedImageRouteCount: replayServer.manifest.imageRoutes.length,
          semanticDigestKey,
          parser: {
            name: PINNED_GFM_PARSER.name,
            version: PINNED_GFM_PARSER.version,
            license: PINNED_GFM_PARSER.license,
            sha256: PINNED_GFM_PARSER.sha256,
          },
          viewport: { width: viewport.width, height: viewport.height, deviceScaleFactor: viewport.deviceScaleFactor },
          colorScheme,
        } },
        { value: MAX_IMAGE_WAIT_MS },
        { value: MAX_REPLAY_STEPS },
        { value: viewportStepRatio },
        { value: nativeViewportProfile },
      ],
      awaitPromise: true,
      returnByValue: true,
      userGesture: false,
    });
    if (result.exceptionDetails) {
      const error = new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
      try {
        Object.defineProperty(error, "sourceReplayStage", {
          value: "source-function-exception",
          configurable: true,
        });
        Object.defineProperty(error, "sourceReplayErrorType", {
          value: result.exceptionDetails.exception?.className,
          configurable: true,
        });
      } catch {}
      throw error;
    }
    const replay = result.result?.value;
    if (!replay || replay.ok !== true) {
      const error = new Error(replay?.error || "Source-bound Edge replay returned no successful result.");
      try {
        Object.defineProperty(error, "sourceReplayStage", {
          value: replay?.failureStage,
          configurable: true,
        });
        Object.defineProperty(error, "sourceReplayErrorType", {
          value: replay?.errorType,
          configurable: true,
        });
        Object.defineProperty(error, "sourceReplayImageDecodeEvidence", {
          value: replay?.imageDecodeEvidence,
          configurable: true,
        });
        Object.defineProperty(error, "sourceReplayImageMapEvidence", {
          value: replay?.imageMapEvidence,
          configurable: true,
        });
        if (SOURCE_REPLAY_FAILURE_PREDICATES.has(replay?.failurePredicate)) {
          Object.defineProperty(error, "sourceReplayFailurePredicate", {
            value: replay.failurePredicate,
            configurable: true,
          });
        }
      } catch {}
      throw error;
    }

    failureStage = "verify-request-isolation";
    await new Promise(resolve => setTimeout(resolve, 100));
    const blockedResourceSet = new Set(blockedExternalUrls);
    for (const blockedUri of replay.blockedExternalUris || []) {
      if (/^(?:https?:|file:|ftp:|ws:|wss:|blob:)/iu.test(blockedUri) &&
          !blockedUri.startsWith(`${origin}/`)) {
        blockedResourceSet.add(blockedUri);
      }
    }
    if (fetchFailure) throw new Error(`Source-bound Edge request isolation failed: ${fetchFailure}`);
    if (blockedResourceSet.size !== 0) {
      const blockedEvidence = [...blockedResourceSet].map(value => {
        let originEvidence = "opaque";
        try {
          const parsed = new URL(value);
          originEvidence = `scheme=${parsed.protocol} hostHash=${sha256(Buffer.from(parsed.hostname, "utf8"))}`;
        } catch {}
        return `${originEvidence} urlHash=${sha256(Buffer.from(value, "utf8"))}`;
      });
      throw new Error(`Source-bound Edge replay attempted ${blockedResourceSet.size} external resource request(s): ${blockedEvidence.join(", ")}.`);
    }

    const tileHeight = Math.min(Math.max(viewport.height, 8192), Math.ceil(replay.height));
    const tileCount = Math.max(1, Math.ceil(replay.height / tileHeight));
    if (tileCount > maximumTiles) {
      throw new Error(`Source-bound replay needs ${tileCount} browser tiles, above the ${maximumTiles} safety ceiling.`);
    }
    failureStage = "capture-tiles";
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
      const file = `same-byte-source-tile-${String(index).padStart(4, "0")}.png`;
      await writeFile(path.join(outputDirectory, file), Buffer.from(capture.data, "base64"));
      tiles.push({ index, relativeY, width: replay.width, height, file });
    }
    validateTileCoverage(tiles, replay.width, replay.height);

    failureStage = "verify-captured-assets";
    const expectedUrlHashes = [...replay.expectedUrlHashes].sort();
    const servedUrlHashes = replayServer.servedUrlSha256s;
    if (replayServer.misses !== 0 ||
        replayServer.servedParserCount !== 1 ||
        replay.verifiedImageCount !== replay.expectedImageCount ||
        expectedUrlHashes.length !== servedUrlHashes.length ||
        expectedUrlHashes.some((value, index) => value !== servedUrlHashes[index])) {
      throw new Error("Source-bound Edge replay did not verify every parser, image asset, and mapped URL hash exactly.");
    }

    return {
      schemaVersion: 3,
      status: "passed",
      source: {
        readmeGitBlobSha1: replayServer.readmeGitBlobSha1,
        readmeSha256: replayServer.readmeSha256,
        byteSize: replayServer.readmeBytes.length,
      },
      parser: {
        name: PINNED_GFM_PARSER.name,
        version: PINNED_GFM_PARSER.version,
        license: PINNED_GFM_PARSER.license,
        sha256: PINNED_GFM_PARSER.sha256,
      },
      viewport: {
        width: viewport.width,
        height: viewport.height,
        deviceScaleFactor: viewport.deviceScaleFactor,
        colorScheme: replay.viewport.colorScheme,
        edgeInnerWidth: replay.viewport.width,
        edgeInnerHeight: replay.viewport.height,
        edgeDeviceScaleFactor: replay.viewport.deviceScaleFactor,
      },
      assets: {
        assetUrlMapSha256: replayServer.assetUrlMapSha256,
        expectedImageCount: replay.expectedImageCount,
        verifiedImageCount: replay.verifiedImageCount,
        firstViewportRealizedImageCount: replay.firstViewportRealizedImageCount,
        distinctExpectedUrlHashes: expectedUrlHashes.length,
        distinctServedUrlHashes: servedUrlHashes.length,
        replayMissCount: replayServer.misses,
        blockedExternalRequestCount: blockedResourceSet.size,
        semanticImageAliasMatchCount: replay.semanticImageAliasMatchCount,
      },
      timing: {
        firstViewportPaintMs: replay.timing.firstViewportPaintMs,
        firstViewportImagesReadyMs: replay.timing.firstViewportImagesReadyMs,
        fullTraversalMs: replay.timing.fullTraversalMs,
        chargedTraversalMs: replay.timing.chargedTraversalMs,
        auditOnlyFrameWaitMs: replay.timing.auditOnlyFrameWaitMs,
        auditOnlyFrameWaitCount: replay.timing.auditOnlyFrameWaitCount,
        viewportStepRatio: replay.timing.viewportStepRatio,
        traversalViewportCount: replay.timing.traversalViewportCount,
        movementCount: replay.timing.movementCount,
        sourceTailViewportCount: replay.timing.sourceTailViewportCount,
        sourceTailMovementCount: replay.timing.sourceTailMovementCount,
        sourceTailCaptureOffsetsViewportUnits: replay.timing.sourceTailCaptureOffsetsViewportUnits,
        viewportProfileSha256: replay.timing.viewportProfileSha256,
        captureOffsetsViewportUnits: replay.timing.captureOffsetsViewportUnits,
        firstViewportBoundary: "exact captured README bytes decoded, pinned Marked GFM parse sanitized and inserted; two requestAnimationFrames elapsed",
        firstViewportImagesReadyBoundary: "same start through successful decode of every visible pinned image",
        fullTraversalBoundary: "raw same start through bottom traversal, visible-image decode at each step, and final paint, including stability and terminal proof rAF waits",
        chargedTraversalBoundary: "same start through parse, insertion, first paint, each movement's first target-matching sample, all image decode work, and source-tail capture; subtracts only measured rAF wait time from successful post-target stability confirmations and final terminal proof frames",
        auditOnlyFrameWaitBoundary: "measured requestAnimationFrame wait intervals used only by successful post-target stability confirmations and final terminal proof frames; image decode waits remain charged",
      },
      renderedExtent: { width: replay.width, height: replay.height },
      semantic: replay.semantic,
      tiles,
    };
  } catch (error) {
    const replayError = error instanceof Error
      ? error
      : new Error("Source-bound Edge replay rejected a non-Error failure value.");
    try {
      if (!SOURCE_REPLAY_STAGES.has(replayError.sourceReplayStage)) {
        Object.defineProperty(replayError, "sourceReplayStage", {
          value: failureStage,
          configurable: true,
        });
      }
    } catch {}
    throw replayError;
  } finally {
    try {
      await cdp.send("Runtime.evaluate", {
        expression: `(() => {
          const urls = window.__jithubSourceReplayObjectUrls || [];
          for (const url of urls) URL.revokeObjectURL(url);
          delete window.__jithubSourceReplayObjectUrls;
        })()`,
        returnByValue: true,
      });
    } catch {}
    removeFetchListener?.();
    try { await cdp.send("Fetch.disable"); } catch {}
    try { await cdp.send("Network.setCacheDisabled", { cacheDisabled: false }); } catch {}
  }
}

function validateReplayArguments({
  cdp, replayServer, outputDirectory, viewport, colorScheme, semanticDigestKey,
  maximumTiles, viewportStepRatio, nativeViewportProfile,
}) {
  if (!cdp || !replayServer?.baseUrl || !replayServer.manifest ||
      !replayServer.readmeBytes || !outputDirectory ||
      !Number.isSafeInteger(viewport?.width) || viewport.width <= 0 ||
      !Number.isSafeInteger(viewport?.height) || viewport.height <= 0 ||
      !Number.isFinite(viewport?.deviceScaleFactor) || viewport.deviceScaleFactor <= 0 ||
      viewport.deviceScaleFactor > 8 ||
      !/^[0-9a-f]{64}$/iu.test(semanticDigestKey || "") ||
      !["light", "dark"].includes(colorScheme) ||
      !Number.isSafeInteger(maximumTiles) || maximumTiles <= 0 || maximumTiles > DEFAULT_MAXIMUM_TILES ||
      !Number.isFinite(viewportStepRatio) || viewportStepRatio <= 0 || viewportStepRatio >= 1 ||
      (nativeViewportProfile !== null && !validateNativeViewportProfile(nativeViewportProfile, viewport.height))) {
    throw new Error("Source-bound Edge replay requires a validated corpus, native viewport, theme, semantic digest key, and bounded output target.");
  }
  if (replayServer.parser?.sha256 !== PINNED_GFM_PARSER.sha256 ||
      replayServer.parser?.version !== PINNED_GFM_PARSER.version) {
    throw new Error("Source-bound Edge replay parser pin does not match the vendored Marked bundle.");
  }
}

export function createUniqueCapturedImageAltRouteMap(sourceAltSha256s, capturedImageAltIdentities, capturedRouteCount) {
  if (!Array.isArray(sourceAltSha256s) || !Array.isArray(capturedImageAltIdentities) ||
      !Number.isSafeInteger(capturedRouteCount) || capturedRouteCount <= 0 ||
      sourceAltSha256s.length !== capturedRouteCount ||
      capturedImageAltIdentities.length !== capturedRouteCount) {
    return null;
  }
  const sourceAltSet = new Set();
  for (const hash of sourceAltSha256s) {
    if (!/^[0-9a-f]{64}$/iu.test(hash || "") || sourceAltSet.has(hash.toLowerCase())) return null;
    sourceAltSet.add(hash.toLowerCase());
  }
  const capturedIndexByAltHash = new Map();
  const capturedIndexes = new Set();
  for (const identity of capturedImageAltIdentities) {
    const hash = identity?.altSha256;
    if (!Number.isSafeInteger(identity?.index) || identity.index < 0 ||
        !/^[0-9a-f]{64}$/iu.test(hash || "") ||
        capturedIndexes.has(identity.index) || capturedIndexByAltHash.has(hash.toLowerCase())) {
      return null;
    }
    capturedIndexes.add(identity.index);
    capturedIndexByAltHash.set(hash.toLowerCase(), identity.index);
  }
  if (sourceAltSet.size !== capturedIndexByAltHash.size ||
      [...sourceAltSet].some(hash => !capturedIndexByAltHash.has(hash))) {
    return null;
  }
  return new Map([...sourceAltSet].map(hash => [hash, capturedIndexByAltHash.get(hash)]));
}

export function validateNativeViewportProfile(profile, viewportHeight) {
  if (!profile || profile.schemaVersion !== 1 || profile.viewportHeight !== viewportHeight ||
      !Array.isArray(profile.viewports) || profile.viewports.length < 1 ||
      profile.viewports.length > MAX_REPLAY_STEPS) {
    return false;
  }
  let previousCaptureOffset = 0;
  for (let index = 0; index < profile.viewports.length; index++) {
    const viewport = profile.viewports[index];
    if (!viewport || !Number.isFinite(viewport.captureOffsetViewportUnits) ||
        viewport.captureOffsetViewportUnits < 0 ||
        !Array.isArray(viewport.movementOffsetsViewportUnits) ||
        viewport.movementOffsetsViewportUnits.length > 17 ||
        viewport.movementOffsetsViewportUnits.some(offset =>
          !Number.isFinite(offset) || offset < 0 || offset > 100_000)) {
      return false;
    }
    if (index === 0) {
      if (Math.abs(viewport.captureOffsetViewportUnits) > 1 / viewportHeight + 0.000001 ||
          viewport.movementOffsetsViewportUnits.length !== 0) return false;
    } else {
      const step = viewport.captureOffsetViewportUnits - previousCaptureOffset;
      if (viewport.movementOffsetsViewportUnits.length < 1 ||
          Math.abs(viewport.movementOffsetsViewportUnits.at(-1) - viewport.captureOffsetViewportUnits) >
            1 / viewportHeight + 0.000001 ||
          step <= 0 || step > 0.9 + 0.000001) return false;
    }
    previousCaptureOffset = viewport.captureOffsetViewportUnits;
  }
  return true;
}

export function validateNativeCaptureObservation(
  actualOffset,
  expectedOffset,
  previousActualOffset,
  positionTolerance,
  maximumStepRatio,
) {
  const positionMatches = Number.isFinite(actualOffset) && Number.isFinite(expectedOffset) &&
    Math.abs(actualOffset - expectedOffset) <= positionTolerance;
  const actualStep = actualOffset - previousActualOffset;
  const stepPreservesOverlap = Number.isFinite(actualStep) && actualStep > 0 &&
    actualStep <= maximumStepRatio + 2 * positionTolerance;
  return { positionMatches, stepPreservesOverlap };
}

export function nextSourceTailOffset(currentOffset, maximumOffset, viewportStepRatio) {
  if (!Number.isFinite(currentOffset) || !Number.isFinite(maximumOffset) ||
      !Number.isFinite(viewportStepRatio) || viewportStepRatio <= 0 || viewportStepRatio >= 1) {
    throw new Error("Source-tail traversal received invalid bounded offsets.");
  }
  if (maximumOffset <= currentOffset) return null;
  return Math.min(maximumOffset, currentOffset + viewportStepRatio);
}

async function loadNativeViewportProfile(profilePath, viewportHeight) {
  if (!profilePath) return null;
  const resolvedPath = path.resolve(profilePath);
  const fileInfo = await lstat(resolvedPath);
  if (!fileInfo.isFile() || fileInfo.isSymbolicLink() || fileInfo.size <= 0 || fileInfo.size > 4 * 1024 * 1024) {
    throw new Error("Native viewport profile is missing, linked, or over its bounded size.");
  }
  const bytes = await readFile(resolvedPath);
  const profile = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  if (!validateNativeViewportProfile(profile, viewportHeight)) {
    throw new Error("Native viewport profile failed its bounded overlap and monotonicity checks.");
  }
  return { ...profile, sha256: sha256(bytes) };
}

export function readCapturedInlineImageEvidence(replayServer) {
  const dataRoutes = (replayServer.imageRoutes || []).filter(route => route?.dataUri === true);
  if (dataRoutes.length === 0) return [];
  const htmlBytes = replayServer.renderedHtmlBytes;
  if (!Buffer.isBuffer(htmlBytes) || htmlBytes.length <= 0 || htmlBytes.length > 32 * 1024 * 1024 ||
      sha256(htmlBytes) !== replayServer.renderedHtmlSha256) {
    throw new Error("Captured GitHub HTML for inline image verification is missing or changed.");
  }

  const imageAttributesByIndex = new Map();
  const html = htmlBytes.toString("utf8");
  for (const match of html.matchAll(/<img\b([^>]*)>/giu)) {
    const indexText = readQuotedAttribute(match[1], "data-jithub-image-index");
    if (indexText === null) continue;
    const index = Number(indexText);
    if (!Number.isSafeInteger(index) || index < 0 || imageAttributesByIndex.has(index)) {
      throw new Error("Captured GitHub HTML contains a duplicate or invalid inline-image index.");
    }
    imageAttributesByIndex.set(index, readQuotedAttribute(match[1], "data-jithub-image-data"));
  }

  let totalInlineBytes = 0;
  return dataRoutes.map(route => {
    if (!Number.isSafeInteger(route.index) || route.index < 0) {
      throw new Error("Captured GitHub HTML contains an invalid inline-image route.");
    }
    const dataUri = imageAttributesByIndex.get(route.index);
    if (typeof dataUri !== "string" || !/^data:image\//iu.test(decodeHtmlAttribute(dataUri))) {
      throw new Error("Captured GitHub HTML is missing a routed inline-image payload.");
    }
    const decoded = decodeInlineImageDataUri(decodeHtmlAttribute(dataUri));
    totalInlineBytes += decoded.bytes.length;
    if (totalInlineBytes > MAX_INLINE_IMAGE_TOTAL_BYTES) {
      throw new Error("Captured inline images exceed the bounded aggregate byte limit.");
    }
    return { mimeType: decoded.mimeType, sha256: sha256(decoded.bytes) };
  });
}

function readQuotedAttribute(attributes, name) {
  const pattern = new RegExp(`\\b${name}\\s*=\\s*(["'])(.*?)\\1`, "iu");
  return pattern.exec(attributes)?.[2] ?? null;
}

function decodeHtmlAttribute(value) {
  return value.replace(/&(#(?:x[0-9a-f]+|[0-9]+)|amp|quot|apos|lt|gt);/giu, (entity, value) => {
    const normalized = value.toLowerCase();
    if (normalized === "amp") return "&";
    if (normalized === "quot") return "\"";
    if (normalized === "apos") return "'";
    if (normalized === "lt") return "<";
    if (normalized === "gt") return ">";
    const scalar = normalized.startsWith("#x")
      ? Number.parseInt(normalized.slice(2), 16)
      : Number.parseInt(normalized.slice(1), 10);
    if (!Number.isInteger(scalar) || scalar <= 0 || scalar > 0x10ffff ||
        (scalar >= 0xd800 && scalar <= 0xdfff)) {
      throw new Error("Captured GitHub HTML has an invalid character reference in an inline image.");
    }
    return String.fromCodePoint(scalar);
  });
}

function decodeInlineImageDataUri(value) {
  if (typeof value !== "string" || Buffer.byteLength(value, "utf8") > MAX_INLINE_IMAGE_URI_BYTES) {
    throw new Error("Inline image URL exceeds the bounded byte limit.");
  }
  const match = /^data:(image\/[a-z0-9.+-]+)(?:;charset=utf-8)?(;base64)?,([\s\S]*)$/iu.exec(value);
  const mimeType = match?.[1]?.toLowerCase();
  if (!match || !SOURCE_IMAGE_MIMES.has(mimeType)) {
    throw new Error("Inline image has an unsupported data URI format.");
  }
  const payload = match[3];
  if (match[2]) {
    if (!payload || !/^(?:[a-z0-9+/]{4})*(?:[a-z0-9+/]{2}(?:==)?|[a-z0-9+/]{3}=?)?$/iu.test(payload)) {
      throw new Error("Inline image contains malformed base64 data.");
    }
    const bytes = Buffer.from(payload, "base64");
    if (bytes.length > MAX_INLINE_IMAGE_URI_BYTES) {
      throw new Error("Inline image exceeds the decoded byte limit.");
    }
    return { mimeType, bytes };
  }
  if (mimeType !== "image/svg+xml") {
    throw new Error("Non-base64 inline image data is supported only for UTF-8 SVG.");
  }
  const encoded = Buffer.from(payload, "utf8");
  if (encoded.length > MAX_INLINE_IMAGE_URI_BYTES) {
    throw new Error("Inline SVG exceeds the encoded byte limit.");
  }
  const decoded = Buffer.allocUnsafe(encoded.length);
  let written = 0;
  for (let index = 0; index < encoded.length;) {
    if (encoded[index] === 0x25) {
      if (index + 2 >= encoded.length) throw new Error("Inline SVG contains an incomplete percent escape.");
      const high = hexValue(encoded[index + 1]);
      const low = hexValue(encoded[index + 2]);
      if (high < 0 || low < 0) throw new Error("Inline SVG contains an invalid percent escape.");
      decoded[written++] = (high << 4) | low;
      index += 3;
    } else {
      decoded[written++] = encoded[index++];
    }
  }
  return { mimeType, bytes: decoded.subarray(0, written) };
}

export function validateTileCoverage(tiles, width, height) {
  if (!Number.isSafeInteger(width) || width <= 0 || !Number.isSafeInteger(height) || height <= 0 ||
      !Array.isArray(tiles) || tiles.length < 1 || tiles.length > DEFAULT_MAXIMUM_TILES) {
    throw new Error("Source-bound Edge replay has an invalid rendered extent or tile list.");
  }
  const ordered = [...tiles].sort((left, right) => left.relativeY - right.relativeY);
  let coveredUntil = 0;
  for (const tile of ordered) {
    if (tile.width !== width || !Number.isSafeInteger(tile.relativeY) || tile.relativeY < 0 ||
        !Number.isSafeInteger(tile.height) || tile.height <= 0 || tile.relativeY + tile.height > height ||
        tile.relativeY > coveredUntil) {
      throw new Error("Source-bound Edge tiles do not continuously cover the complete rendered extent.");
    }
    coveredUntil = Math.max(coveredUntil, tile.relativeY + tile.height);
  }
  if (coveredUntil !== height) {
    throw new Error("Source-bound Edge tiles do not reach the end of the rendered document.");
  }
}

function hexValue(value) {
  if (value >= 0x30 && value <= 0x39) return value - 0x30;
  if (value >= 0x41 && value <= 0x46) return value - 0x41 + 10;
  if (value >= 0x61 && value <= 0x66) return value - 0x61 + 10;
  return -1;
}

const SOURCE_REPLAY_FAILURE_MESSAGES = Object.freeze({
  "browser-startup": "Edge could not start or expose its replay context.",
  "external-resource": "The Markdown replay attempted a disallowed external resource.",
  integrity: "Captured Markdown, parser, manifest, or image evidence failed validation.",
  image: "A captured Markdown image could not be verified or decoded.",
  "resource-limit": "The replay exceeded a configured resource or traversal limit.",
  replay: "The source-bound Markdown replay failed.",
  timeout: "The source-bound Markdown replay exceeded its deadline.",
});

const SOURCE_REPLAY_IMAGE_DECODE_OUTCOMES = new Set([
  "already-complete-empty",
  "decoded",
  "decode-empty",
  "decode-rejected",
  "load-empty",
  "load-error",
  "timeout",
]);

function sanitizeSourceReplayImageDecodeEvidence(value) {
  if (!value || !Number.isSafeInteger(value.visibleImageCount) || value.visibleImageCount < 0 ||
      !Number.isSafeInteger(value.failedVisibleImageCount) || value.failedVisibleImageCount < 0 ||
      value.failedVisibleImageCount > value.visibleImageCount || !Array.isArray(value.images)) {
    return null;
  }
  const images = [];
  const seenIndexes = new Set();
  for (const item of value.images.slice(0, 64)) {
    if (!item || !Number.isSafeInteger(item.imageIndex) || item.imageIndex < 0 ||
        seenIndexes.has(item.imageIndex) || typeof item.dataUri !== "boolean" ||
        typeof item.ready !== "boolean" ||
        !SOURCE_REPLAY_IMAGE_DECODE_OUTCOMES.has(item.decodeOutcome)) {
      continue;
    }
    if (item.dataUri) {
      if (item.capturedUrlSha256 !== undefined) continue;
      images.push({
        imageIndex: item.imageIndex,
        dataUri: true,
        ready: item.ready,
        decodeOutcome: item.decodeOutcome,
      });
    } else {
      if (!/^[0-9a-f]{64}$/iu.test(item.capturedUrlSha256 || "")) continue;
      images.push({
        imageIndex: item.imageIndex,
        capturedUrlSha256: item.capturedUrlSha256.toLowerCase(),
        dataUri: false,
        ready: item.ready,
        decodeOutcome: item.decodeOutcome,
      });
    }
    seenIndexes.add(item.imageIndex);
  }
  return {
    visibleImageCount: value.visibleImageCount,
    failedVisibleImageCount: value.failedVisibleImageCount,
    images,
    omittedVisibleImageCount: Math.max(0, value.visibleImageCount - images.length),
  };
}

function sanitizeSourceReplayImageMapEvidence(value) {
  if (!value || !Number.isSafeInteger(value.imageIndex) || value.imageIndex < 0 ||
      !Number.isSafeInteger(value.capturedAssetUrlEntryCount) || value.capturedAssetUrlEntryCount < 0 ||
      !Array.isArray(value.candidateUrlSha256s)) {
    return null;
  }
  const candidateUrlSha256s = value.candidateUrlSha256s.slice(0, 8)
    .filter(hash => /^[0-9a-f]{64}$/iu.test(hash || ""))
    .map(hash => hash.toLowerCase());
  if (candidateUrlSha256s.length === 0) return null;
  return {
    imageIndex: value.imageIndex,
    candidateUrlSha256s,
    capturedAssetUrlEntryCount: value.capturedAssetUrlEntryCount,
  };
}

export function createSourceReplayFailureDiagnostic(error) {
  const message = String(error?.message || error || "");
  let category = "replay";
  if (error?.sourceReplayStage === "visible-image-decode") {
    category = "image";
  } else if (error?.name === "TimeoutError" || /timed out|timeout|deadline/iu.test(message)) {
    category = "timeout";
  } else if (/external resource|request isolation|blocked external/iu.test(message)) {
    category = "external-resource";
  } else if (/safety limit|over-budget|exceed(?:ed|s)? .*limit|tile ceiling|step count/iu.test(message)) {
    category = "resource-limit";
  } else if (/hash|digest|manifest|pinned|captured .*bytes|identity mismatch/iu.test(message)) {
    category = "integrity";
  } else if (/image|data uri|decode/iu.test(message)) {
    category = "image";
  } else if (/devtools|websocket|edge did not expose|edge could not start/iu.test(message)) {
    category = "browser-startup";
  }
  const stage = SOURCE_REPLAY_STAGES.has(error?.sourceReplayStage) ? error.sourceReplayStage : "initialization";
  const failurePredicate = SOURCE_REPLAY_FAILURE_PREDICATES.has(error?.sourceReplayFailurePredicate) &&
    ((stage === "sanitize-markdown" && ["invalid-image-dimension", "responsive-image-candidates", "sanitized-image-count"].includes(error.sourceReplayFailurePredicate)) ||
     (stage === "image-map" && ["conflicting-image-aliases", "invalid-image-url", "invalid-inline-image-data", "inline-image-byte-mismatch", "missing-captured-image-alias", "unsupported-image-url-scheme"].includes(error.sourceReplayFailurePredicate)) ||
     (stage === "inline-image-evidence" && error.sourceReplayFailurePredicate === "unmatched-inline-image-evidence"))
    ? error.sourceReplayFailurePredicate
    : null;
  const imageDecodeEvidence = category === "image" && stage === "visible-image-decode"
    ? sanitizeSourceReplayImageDecodeEvidence(error?.sourceReplayImageDecodeEvidence)
    : null;
  const imageMapEvidence = category === "image" && stage === "image-map" &&
    failurePredicate === "missing-captured-image-alias"
    ? sanitizeSourceReplayImageMapEvidence(error?.sourceReplayImageMapEvidence)
    : null;
  return {
    category,
    message: SOURCE_REPLAY_FAILURE_MESSAGES[category],
    stage,
    errorType: SOURCE_REPLAY_ERROR_TYPES.has(error?.sourceReplayErrorType)
      ? error.sourceReplayErrorType
      : SOURCE_REPLAY_ERROR_TYPES.has(error?.name) ? error.name : "Error",
    failurePredicate,
    imageDecodeEvidence,
    imageMapEvidence,
  };
}

export function createSourceReplayFailureReport(error) {
  const diagnostic = createSourceReplayFailureDiagnostic(error);
  return {
    schemaVersion: 2,
    status: "failed",
    failureCategory: diagnostic.category,
    error: diagnostic.message,
    failureStage: diagnostic.stage,
    errorType: diagnostic.errorType,
    ...(diagnostic.failurePredicate ? { failurePredicate: diagnostic.failurePredicate } : {}),
    ...(diagnostic.imageDecodeEvidence ? { imageDecodeEvidence: diagnostic.imageDecodeEvidence } : {}),
    ...(diagnostic.imageMapEvidence ? { imageMapEvidence: diagnostic.imageMapEvidence } : {}),
  };
}

function githubReadmePageUrl(repository) {
  const [owner, name] = String(repository.fullName || "").split("/");
  const rawPathSegments = String(repository.readmePath || "").split("/");
  const pathSegments = rawPathSegments.map(encodeURIComponent);
  if (!owner || !name || !/^[0-9a-f]{40}$/iu.test(repository.commitSha || "") ||
      pathSegments.length === 0 || rawPathSegments.some(segment =>
        !segment || segment === "." || segment === ".." || segment.includes("\\") || segment.includes("\0"))) {
    throw new Error("Source-bound Edge replay corpus has no safe pinned README URL identity.");
  }
  return `https://github.com/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/blob/${repository.commitSha}/${pathSegments.join("/")}`;
}

export function sourceReplayHmacDigestFunctionDeclaration() {
  return String.raw`async function(keyHex) {
    if (!/^[0-9a-f]{64}$/iu.test(keyHex || "")) {
      throw new Error("Source semantic digest key is invalid.");
    }
    const keyBytes = Uint8Array.from(keyHex.match(/.{2}/gu).map(value => Number.parseInt(value, 16)));
    const key = await crypto.subtle.importKey(
      "raw", keyBytes, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
    return async function(value) {
      const digest = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(String(value || "")));
      return [...new Uint8Array(digest)].map(item => item.toString(16).padStart(2, "0")).join("");
    };
  }`;
}

export function sourceReplayTokenDigestFunctionDeclaration() {
  return String.raw`async function(text, digestSemanticText) {
    if (typeof digestSemanticText !== "function") {
      throw new Error("Source semantic HMAC digester is unavailable.");
    }
    const counts = new Map();
    // Match ReadmeAuditProbe.CountTokens: letters, numbers, and only
    // non-spacing/spacing combining marks (not enclosing marks).
    const runs = String(text || "").match(/[\p{L}\p{N}\p{Mn}\p{Mc}]+/gu) || [];
    const add = token => {
      // .NET ToLowerInvariant uses simple, per-scalar mappings. JavaScript's
      // string lowercasing applies contextual sigma rules and expands U+0130,
      // so normalize each scalar independently and preserve U+0130 (which
      // has no simple lowercase mapping in the native tokenizer).
      const normalized = [...token.normalize("NFC")]
        .map(rune => rune === "\u0130" ? rune : rune.toLowerCase())
        .join("");
      counts.set(normalized, (counts.get(normalized) || 0) + 1);
    };
    for (const run of runs) {
      let buffered = "";
      const flush = () => {
        if (!buffered) return;
        add(buffered);
        buffered = "";
      };
      for (const rune of run) {
        const scalar = rune.codePointAt(0);
        if ((scalar >= 0x3400 && scalar <= 0x4dbf) ||
            (scalar >= 0x4e00 && scalar <= 0x9fff) ||
            (scalar >= 0xf900 && scalar <= 0xfaff) ||
            (scalar >= 0x20000 && scalar <= 0x2fa1f)) {
          flush();
          add(rune);
        } else {
          buffered += rune;
        }
      }
      flush();
    }
    if (counts.size > 20000) {
      throw new Error("Source semantic token histogram exceeded its 20000-entry safety limit.");
    }
    const entries = await Promise.all([...counts].map(async ([token, count]) =>
      [await digestSemanticText(token), count]));
    return Object.fromEntries(entries);
  }`;
}

export function sourceReplayFunctionDeclaration() {
  return String.raw`async function(baseUrl, expected, imageWaitMs, maximumSteps, viewportStepRatio, nativeViewportProfile) {
    const blockedExternalUris = new Set();
    const inlineObjectUrls = [];
    const imageDecodeEvidenceByElement = new WeakMap();
    let retainInlineObjectUrls = false;
    let imageDecodeEvidence = null;
    let failureStage = "viewport";
    const isExternalResource = value => /^(?:https?:|file:|ftp:|ws:|wss:|blob:)/iu.test(value || "");
    const onPolicyViolation = event => {
      if (isExternalResource(event.blockedURI)) blockedExternalUris.add(event.blockedURI);
    };
    addEventListener("securitypolicyviolation", onPolicyViolation);
    const hashBytes = async bytes => {
      const digest = await crypto.subtle.digest("SHA-256", bytes);
      return [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, "0")).join("");
    };
    const hashText = value => hashBytes(new TextEncoder().encode(value));
    const fail = (message, failurePredicate, failureEvidence) => {
      const error = new Error(message);
      if (["conflicting-image-aliases", "invalid-image-dimension", "invalid-image-url", "invalid-inline-image-data",
           "inline-image-byte-mismatch", "missing-captured-image-alias", "responsive-image-candidates",
           "sanitized-image-count", "unmatched-inline-image-evidence", "unsupported-image-url-scheme"]
          .includes(failurePredicate)) error.sourceReplayFailurePredicate = failurePredicate;
      if (failurePredicate === "missing-captured-image-alias" && failureEvidence) {
        error.sourceReplayImageMapEvidence = failureEvidence;
      }
      throw error;
    };
    const createUniqueCapturedImageAltRouteMap = (${createUniqueCapturedImageAltRouteMap.toString()});
    const validateNativeCaptureObservation = (${validateNativeCaptureObservation.toString()});
    const nextSourceTailOffset = (${nextSourceTailOffset.toString()});
    const createSemanticHmacDigester = (${sourceReplayHmacDigestFunctionDeclaration()});
    const createSemanticTokenDigestCounts = (${sourceReplayTokenDigestFunctionDeclaration()});
    try {
      const digestSemanticText = await createSemanticHmacDigester(expected.semanticDigestKey);
      if (innerWidth !== expected.viewport.width || innerHeight !== expected.viewport.height ||
          Math.abs(devicePixelRatio - expected.viewport.deviceScaleFactor) > 0.01) {
        fail("Edge content viewport does not match the measured native Markdown host viewport.");
      }
      document.documentElement.dataset.colorScheme = expected.colorScheme;
      document.documentElement.style.colorScheme = expected.colorScheme;
      failureStage = "manifest-response";
      const manifestResponse = await fetch(baseUrl + "/manifest", { cache: "no-store", credentials: "omit", redirect: "error" });
      if (!manifestResponse.ok || manifestResponse.headers.get("x-content-sha256") !== expected.manifestSha256) {
        fail("Edge did not receive the hash-matched pinned same-byte manifest.");
      }
      const manifestBytes = await manifestResponse.arrayBuffer();
      failureStage = "manifest-digest";
      if (await hashBytes(manifestBytes) !== expected.manifestSha256) {
        fail("Edge same-byte manifest digest did not match its pinned capture.");
      }
      const manifest = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(manifestBytes));
      failureStage = "manifest-identity";
      if (manifest.schemaVersion !== 2 || manifest.complete !== true ||
          manifest.repository.readmeGitBlobSha1 !== expected.readmeGitBlobSha1 ||
          manifest.readme.sha256 !== expected.readmeSha256 ||
          manifest.readme.byteSize !== expected.readmeByteSize ||
          manifest.browserRender.status !== "passed") {
        fail("Edge source replay manifest identity does not match the captured README.");
      }
      failureStage = "asset-map";
      if (await hashText(JSON.stringify(manifest.assets)) !== expected.assetUrlMapSha256) {
        fail("Edge asset URL map does not match the captured map digest.");
      }
      const assetByUrlHash = new Map();
      for (const entry of manifest.assets) {
        if (!/^[0-9a-f]{64}$/u.test(entry.urlSha256 || "") ||
            !/^[0-9a-f]{64}$/u.test(entry.sha256 || "") ||
            typeof entry.mimeType !== "string" || assetByUrlHash.has(entry.urlSha256)) {
          fail("Edge captured asset URL map is malformed or ambiguous.");
        }
        assetByUrlHash.set(entry.urlSha256, entry);
      }
      failureStage = "readme-response";
      const readmeResponse = await fetch(baseUrl + "/readme", { cache: "no-store", credentials: "omit", redirect: "error" });
      if (!readmeResponse.ok || readmeResponse.headers.get("x-content-sha256") !== expected.readmeSha256) {
        fail("Edge did not receive the hash-matched raw README bytes.");
      }
      const readmeBytes = await readmeResponse.arrayBuffer();
      failureStage = "readme-digest";
      if (readmeBytes.byteLength !== expected.readmeByteSize || await hashBytes(readmeBytes) !== expected.readmeSha256) {
        fail("Edge raw README bytes do not match the captured SHA-256 or size.");
      }
      failureStage = "parser-pin";
      if (!window.marked || typeof window.marked.parse !== "function" ||
          (window.marked.version && window.marked.version !== expected.parser.version)) {
        fail("Edge did not load the pinned Marked parser bundle.");
      }
      const started = performance.now();
      let auditOnlyFrameWaitMs = 0;
      let auditOnlyFrameWaitCount = 0;
      failureStage = "parse-markdown";
      const markdown = new TextDecoder("utf-8", { fatal: true }).decode(readmeBytes);
      const parsedHtml = window.marked.parse(markdown, { gfm: true, breaks: false, pedantic: false, async: false });
      if (typeof parsedHtml !== "string") fail("Pinned Marked parser returned a non-string result.");
      const parsedDocument = new DOMParser().parseFromString(parsedHtml, "text/html");
      const parsedElements = [...parsedDocument.body.querySelectorAll("*")];
      if (parsedElements.length > 200000) fail("Pinned Markdown parse exceeded the bounded HTML element count.");
      const unsupportedSemanticMarkupCount = parsedDocument.body.querySelectorAll(
        "audio,canvas,embed,form,iframe,math,object,svg,video").length;
      failureStage = "sanitize-markdown";
      const isMarkdownVisibleImage = image => {
        if (image.closest("[hidden]")) return false;
        for (let ancestor = image.parentElement; ancestor; ancestor = ancestor.parentElement) {
          if (ancestor.tagName !== "DETAILS" || ancestor.hasAttribute("open")) continue;
          const summary = ancestor.querySelector(":scope > summary");
          if (!summary?.contains(image)) return false;
        }
        return true;
      };
      const parsedImages = [...parsedDocument.body.querySelectorAll("img")];
      const authoredImageDimensions = new WeakMap();
      for (const image of parsedImages) {
        const dimensions = { width: null, height: null };
        for (const dimension of ["width", "height"]) {
          if (!image.hasAttribute(dimension)) continue;
          const value = image.getAttribute(dimension) || "";
          const match = /^([1-9][0-9]{0,4})(?:px)?$/iu.exec(value);
          const pixels = match ? Number(match[1]) : Number.NaN;
          if (!Number.isSafeInteger(pixels) || pixels > 16384) {
            fail("Markdown image has an invalid authored width or height.", "invalid-image-dimension");
          }
          dimensions[dimension] = pixels;
        }
        authoredImageDimensions.set(image, dimensions);
      }
      const inputImages = parsedImages.filter(isMarkdownVisibleImage);
      for (const image of inputImages) {
        if (image.hasAttribute("srcset") || image.hasAttribute("sizes")) {
            fail("Markdown image has responsive source candidates that cannot be pinned to one captured asset.",
            "responsive-image-candidates");
        }
      }
      const sourceBearingImages = inputImages.filter(image => Boolean(image.getAttribute("src")));
      const imageSources = sourceBearingImages.map(image => image.getAttribute("src"));
      const sourceHrefByAnchor = new WeakMap();
      const allowedTags = new Set(["a","abbr","b","blockquote","br","code","del","details","div","dl","dt","em","h1","h2","h3","h4","h5","h6","hr","i","img","input","ins","kbd","li","mark","ol","p","pre","s","samp","span","strong","sub","summary","sup","table","tbody","td","tfoot","th","thead","tr","ul"]);
      const removeSubtree = new Set(["audio","canvas","embed","form","iframe","math","object","script","source","style","svg","template","track","video"]);
      for (const element of [...parsedDocument.body.querySelectorAll("*")].reverse()) {
        const tag = element.tagName.toLowerCase();
        if (removeSubtree.has(tag)) { element.remove(); continue; }
        if (!allowedTags.has(tag)) {
          element.replaceWith(...element.childNodes);
          continue;
        }
        if (tag === "a" && element.hasAttribute("href")) {
          sourceHrefByAnchor.set(element, element.getAttribute("href") || "");
          element.removeAttribute("href");
        }
        for (const attribute of [...element.attributes]) {
          const name = attribute.name.toLowerCase();
          const allowed = name === "class" || name === "title" || name === "alt" ||
            name === "width" || name === "height" || name === "align" ||
            name === "colspan" || name === "rowspan" || name === "start" ||
            name === "open" || name === "hidden" || name === "checked" || name === "disabled" ||
            name === "type" || name === "data-jithub-image-index";
          if (!allowed || name.startsWith("on")) element.removeAttribute(attribute.name);
        }
        if (tag === "input") {
          if ((element.getAttribute("type") || "").toLowerCase() !== "checkbox") { element.remove(); continue; }
          element.setAttribute("disabled", "");
        }
        if (tag === "img") {
          const dimensions = authoredImageDimensions.get(element);
          const width = dimensions?.width;
          const height = dimensions?.height;
          if (width && height) {
            // Keep the authored aspect ratio if max-width has to shrink the
            // image to the replay viewport. Pixel-unit HTML hints retain their
            // original attributes; their common px unit cancels in the ratio.
            element.style.aspectRatio = width + " / " + height;
            element.style.height = "auto";
          }
          element.removeAttribute("src");
          element.setAttribute("loading", "lazy");
        }
      }
      const article = document.createElement("article");
      article.id = "readme";
      article.className = "markdown-body";
      article.append(...parsedDocument.body.childNodes);
      const sourceBearingImageSet = new Set(sourceBearingImages);
      const images = [...article.querySelectorAll("img")]
        .filter(image => sourceBearingImageSet.has(image) && isMarkdownVisibleImage(image));
      if (images.length !== imageSources.length) {
        fail("Sanitizing the parser output changed its image element count.", "sanitized-image-count");
      }
      const normalizedUrlCandidates = value => {
        let url;
        try { url = new URL(value, expected.readmeBaseUrl); }
        catch { fail("Markdown image URL is invalid.", "invalid-image-url"); }
        if (!((url.protocol === "https:" || url.protocol === "http:") && !url.username && !url.password)) {
          fail("Markdown image uses an unsupported or credentialed URL scheme.", "unsupported-image-url-scheme");
        }
        url.hash = "";
        if (url.hostname.toLowerCase() === "github.com" && url.searchParams.size === 1 && url.searchParams.get("raw") === "true") {
          url.search = "";
        }
        const candidates = new Set([url.href]);
        const segments = url.pathname.split("/").filter(Boolean);
        const hostname = url.hostname.toLowerCase();
        let owner = "";
        let repository = "";
        let commit = "";
        let fileSegments = [];
        if (hostname === "github.com" && segments.length >= 5 &&
            (segments[2] === "blob" || segments[2] === "raw")) {
          owner = segments[0];
          repository = segments[1];
          commit = segments[3];
          fileSegments = segments.slice(4);
        } else if (hostname === "raw.githubusercontent.com" && segments.length >= 4) {
          owner = segments[0];
          repository = segments[1];
          commit = segments[2];
          fileSegments = segments.slice(3);
        }
        if (owner && repository && commit && fileSegments.length > 0) {
          const encodedPath = fileSegments.join("/");
          const encodedIdentity = encodeURIComponent(owner) + "/" + encodeURIComponent(repository);
          const encodedCommit = encodeURIComponent(commit);
          const aliases = [
            new URL("https://github.com/" + encodedIdentity + "/blob/" + encodedCommit + "/" + encodedPath),
            new URL("https://github.com/" + encodedIdentity + "/raw/" + encodedCommit + "/" + encodedPath),
            new URL("https://raw.githubusercontent.com/" + encodedIdentity + "/" + encodedCommit + "/" + encodedPath),
          ];
          for (const alias of aliases) {
            alias.search = url.search;
            alias.hash = "";
            if (alias.hostname === "github.com" && alias.searchParams.size === 1 && alias.searchParams.get("raw") === "true") {
              alias.search = "";
            }
            candidates.add(alias.href);
          }
        }
        return [...candidates];
      };
      const expectedUrlHashes = new Set();
      const pendingImageSources = new Map();
      const sourceImageAltHashes = await Promise.all(images.map(async image => {
        const alt = image.getAttribute("alt") || "";
        return alt.length === 0 ? null : await hashText(alt.normalize("NFC"));
      }));
      const semanticAltRouteMap = createUniqueCapturedImageAltRouteMap(
        sourceImageAltHashes,
        expected.capturedImageAltIdentities,
        expected.capturedImageRouteCount,
      );
      const capturedRouteByIndex = new Map(manifest.imageRoutes.map(route => [route.index, route]));
      let semanticImageAliasMatchCount = 0;
      const remainingInlineImages = new Map();
      const inlineImageDigests = [];
      for (const item of expected.capturedInlineImageEvidence || []) {
        const key = item.mimeType + ":" + item.sha256;
        remainingInlineImages.set(key, (remainingInlineImages.get(key) || 0) + 1);
      }
      let inlineImageBytes = 0;
      failureStage = "image-map";
      for (let index = 0; index < images.length; index++) {
        images[index].setAttribute("data-jithub-image-index", String(index));
        if (/^data:image\//iu.test(imageSources[index])) {
          const decoded = decodeInlineImageDataUri(imageSources[index]);
          inlineImageBytes += decoded.bytes.byteLength;
          if (inlineImageBytes > ${MAX_INLINE_IMAGE_TOTAL_BYTES}) {
            fail("Markdown inline images exceed the bounded aggregate byte limit.");
          }
          const digest = await hashBytes(decoded.bytes);
          const key = decoded.mimeType + ":" + digest;
          const remaining = remainingInlineImages.get(key) || 0;
          if (remaining <= 0) {
            fail("Markdown inline image bytes do not match a captured visible GitHub image.",
              "inline-image-byte-mismatch");
          }
          remainingInlineImages.set(key, remaining - 1);
          inlineImageDigests.push(key);
          const objectUrl = URL.createObjectURL(new Blob([decoded.bytes], { type: decoded.mimeType }));
          inlineObjectUrls.push(objectUrl);
          imageDecodeEvidenceByElement.set(images[index], { imageIndex: index, dataUri: true });
          pendingImageSources.set(images[index], objectUrl);
          continue;
        }
        const candidates = normalizedUrlCandidates(imageSources[index]);
        const candidateHashes = await Promise.all(candidates.map(hashText));
        const matchingEntries = [...new Set(candidateHashes
          .map(urlHash => assetByUrlHash.get(urlHash))
          .filter(Boolean))];
        const distinctPayloads = new Set(matchingEntries.map(entry => entry.sha256 + ":" + entry.mimeType));
        if (distinctPayloads.size > 1) {
          fail("Markdown image URL aliases resolve to conflicting captured payloads.", "conflicting-image-aliases");
        }
        let entry = matchingEntries[0];
        let imageAssetSource = "";
        if (!entry) {
          const sourceAltSha256 = sourceImageAltHashes[index]?.toLowerCase();
          const capturedRouteIndex = sourceAltSha256 && semanticAltRouteMap?.get(sourceAltSha256);
          const capturedRoute = capturedRouteByIndex.get(capturedRouteIndex);
          const capturedEntry = capturedRoute && capturedRoute.dataUri !== true &&
            /^[0-9a-f]{64}$/iu.test(capturedRoute.urlSha256 || "") &&
            /^[0-9a-f]{64}$/iu.test(capturedRoute.sha256 || "")
            ? assetByUrlHash.get(capturedRoute.urlSha256)
            : null;
          if (capturedEntry && capturedEntry.sha256 === capturedRoute.sha256 &&
              capturedEntry.mimeType === capturedRoute.mimeType) {
            entry = capturedEntry;
            imageAssetSource = baseUrl + "/asset-by-content-sha256/" +
              capturedRoute.sha256 + "?index=" + capturedRoute.index;
            semanticImageAliasMatchCount++;
          } else {
            fail("Markdown image asset is missing from the captured URL map.", "missing-captured-image-alias", {
              imageIndex: index,
              candidateUrlSha256s: candidateHashes,
              capturedAssetUrlEntryCount: assetByUrlHash.size,
            });
          }
        }
        expectedUrlHashes.add(entry.urlSha256);
        imageDecodeEvidenceByElement.set(images[index], {
          imageIndex: index,
          capturedUrlSha256: entry.urlSha256,
          dataUri: false,
        });
        pendingImageSources.set(images[index], imageAssetSource ||
          baseUrl + "/asset-by-url-sha256/" + entry.urlSha256);
      }
      if ([...remainingInlineImages.values()].some(count => count !== 0)) {
        failureStage = "inline-image-evidence";
        fail("A captured GitHub inline image is missing from the pinned Markdown source.",
          "unmatched-inline-image-evidence");
      }
      window.__jithubSourceReplayObjectUrls = inlineObjectUrls;
      retainInlineObjectUrls = true;
      document.body.replaceChildren(article);
      document.body.style.margin = "0";
      const nextFrame = () => new Promise(resolve => requestAnimationFrame(() => resolve()));
      const measuredNextFrame = async () => {
        const frameStarted = performance.now();
        await nextFrame();
        return performance.now() - frameStarted;
      };
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
        const loadOutcome = await new Promise(resolve => {
          let settled = false;
          const finish = outcome => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            image.removeEventListener("load", onLoad);
            image.removeEventListener("error", onError);
            resolve(outcome);
          };
          const hasDimensions = () => image.naturalWidth > 0 && image.naturalHeight > 0;
          const onLoad = () => finish(hasDimensions() ? "load" : "load-empty");
          const onError = () => finish("load-error");
          const timer = setTimeout(() => finish("timeout"), imageWaitMs);
          image.addEventListener("load", onLoad, { once: true });
          image.addEventListener("error", onError, { once: true });
          if (image.complete) finish(hasDimensions() ? "already-loaded" : "already-complete-empty");
        });
        if (!["load", "already-loaded"].includes(loadOutcome)) {
          return { ready: false, outcome: loadOutcome };
        }
        try {
          await image.decode();
          return image.complete && image.naturalWidth > 0 && image.naturalHeight > 0
            ? { ready: true, outcome: "decoded" }
            : { ready: false, outcome: "decode-empty" };
        } catch {
          return { ready: false, outcome: "decode-rejected" };
        }
      };
      const visibleImages = () => images.filter(image => {
          const rect = image.getBoundingClientRect();
          const style = getComputedStyle(image);
          return image.getClientRects().length > 0 && style.display !== "none" &&
            style.visibility !== "hidden" && Number(style.opacity) !== 0 &&
            rect.bottom >= 0 && rect.top <= innerHeight &&
            rect.right >= 0 && rect.left <= innerWidth;
      });
      const waitVisibleImages = async () => {
        const previousFailureStage = failureStage;
        failureStage = "visible-image-decode";
        const visible = visibleImages();
        const outcomes = await Promise.all(visible.map(async image => ({
          image,
          ...(await waitImageDecode(image)),
        })));
        const failed = outcomes.filter(result => !result.ready);
        imageDecodeEvidence = {
          visibleImageCount: outcomes.length,
          failedVisibleImageCount: failed.length,
          images: outcomes.slice(0, 64).map(({ image, ready, outcome }) => ({
            ...imageDecodeEvidenceByElement.get(image),
            ready,
            decodeOutcome: outcome,
          })),
        };
        if (failed.length > 0) {
          fail("A visible pinned Markdown image did not decode successfully.");
        }
        failureStage = previousFailureStage;
        return visible.length;
      };
      failureStage = "first-viewport";
      realizeImagesNearViewport();
      await nextFrame();
      await nextFrame();
      const firstViewportPaintMs = performance.now() - started;
      await waitVisibleImages();
      const firstViewportImagesReadyMs = performance.now() - started;
      const firstViewportRealizedImageCount = images.length - pendingImageSources.size;
      if (!Number.isFinite(viewportStepRatio) || viewportStepRatio <= 0 || viewportStepRatio >= 1) {
        fail("Source-bound Edge traversal received an invalid viewport step ratio.");
      }
      let traversalViewportCount = 0;
      let movementCount = 0;
      const captureOffsetsViewportUnits = [];
      let sourceTailViewportCount = 0;
      let sourceTailMovementCount = 0;
      const sourceTailCaptureOffsetsViewportUnits = [];
      let measuredViewportStepRatio = viewportStepRatio;
      const positionTolerance = 1 / innerHeight + 0.000001;
      if (nativeViewportProfile) {
        failureStage = "native-initial-offset";
        const profileViewports = nativeViewportProfile.viewports;
        const initialTop = scrollY / innerHeight;
        if (Math.abs(initialTop - profileViewports[0].captureOffsetViewportUnits) > positionTolerance) {
          fail("Source-bound Edge did not begin at the native profile's first capture offset.");
        }
        captureOffsetsViewportUnits.push(initialTop);
        traversalViewportCount = 1;
        let previousCaptureOffset = initialTop;

        for (let viewportIndex = 1; viewportIndex < profileViewports.length; viewportIndex++) {
          failureStage = "native-movement";
          const viewport = profileViewports[viewportIndex];
          if (traversalViewportCount >= maximumSteps) {
            fail("Source-bound Edge traversal exceeded its bounded native viewport count.");
          }
          for (const movementOffset of viewport.movementOffsetsViewportUnits) {
            if (movementCount >= maximumSteps * 17) {
              fail("Source-bound Edge traversal exceeded its bounded native movement count.");
            }
            const expectedTop = movementOffset;
            scrollTo(0, expectedTop * innerHeight);
            let stable = false;
            let previousHeight = -1;
            let previousTop = Number.NaN;
            let actualTop = Number.NaN;
            for (let pass = 0; pass < 8; pass++) {
              let passFrameWaitMs = await measuredNextFrame();
              realizeImagesNearViewport();
              await waitVisibleImages();
              passFrameWaitMs += await measuredNextFrame();
              realizeImagesNearViewport();
              await waitVisibleImages();
              passFrameWaitMs += await measuredNextFrame();
              actualTop = scrollY / innerHeight;
              const currentHeight = document.documentElement.scrollHeight;
              if (Math.abs(actualTop - expectedTop) > positionTolerance) {
                if (previousHeight >= 0 && currentHeight === previousHeight &&
                    Math.abs(actualTop - previousTop) <= positionTolerance / 4) {
                  fail("Source-bound Edge could not reproduce a settled native movement offset.");
                }
                previousHeight = currentHeight;
                previousTop = actualTop;
                continue;
              }
              if (previousHeight >= 0 && currentHeight === previousHeight &&
                  Math.abs(actualTop - previousTop) <= positionTolerance / 4) {
                // Keep this frame pair's image waits charged, but exclude only
                // the measured rAF wait used for the audit-only confirmation.
                auditOnlyFrameWaitMs += passFrameWaitMs;
                auditOnlyFrameWaitCount += 3;
                stable = true;
                break;
              }
              previousHeight = currentHeight;
              previousTop = actualTop;
            }
            if (!stable || Math.abs(actualTop - expectedTop) > positionTolerance) {
              fail("Source-bound Edge movement did not settle at the recorded native offset.");
            }
            movementCount++;
          }

          failureStage = "native-capture-position";
          const captureTop = scrollY / innerHeight;
          const captureValidation = validateNativeCaptureObservation(
            captureTop,
            viewport.captureOffsetViewportUnits,
            previousCaptureOffset,
            positionTolerance,
            viewportStepRatio,
          );
          if (!captureValidation.positionMatches) {
            fail("Source-bound Edge capture offsets did not match the native viewport profile.");
          }
          failureStage = "native-capture-step";
          if (!captureValidation.stepPreservesOverlap) {
            fail("Source-bound Edge capture offsets did not preserve the native monotonic overlapping coverage profile.");
          }
          captureOffsetsViewportUnits.push(captureTop);
          traversalViewportCount++;
          previousCaptureOffset = captureTop;
        }

        // Keep the native-aligned profile metrics intact, then independently
        // walk any additional Edge-only extent so lazy content is realized and
        // the captured source reaches its own bottom.
        let previousTailOffset = captureOffsetsViewportUnits.at(-1);
        let terminalConfirmed = false;
        let tailPassCount = 0;
        while (!terminalConfirmed) {
          if (++tailPassCount > maximumSteps + 8) {
            failureStage = "source-tail-terminal-confirmation";
            fail("Source-bound Edge source-tail traversal exceeded its bounded confirmation count.");
          }
          failureStage = "source-tail-settle";
          realizeImagesNearViewport();
          await waitVisibleImages();
          await nextFrame();
          await waitVisibleImages();
          await nextFrame();
          realizeImagesNearViewport();
          const actualTop = scrollY / innerHeight;
          const heightBeforeTailDecision = document.documentElement.scrollHeight;
          let maxTop = Math.max(0, heightBeforeTailDecision - innerHeight) / innerHeight;
          if (maxTop - actualTop <= positionTolerance) {
            failureStage = "source-tail-terminal-images";
            if (pendingImageSources.size !== 0) {
              fail("Source-bound traversal reached the source bottom with captured images outside the realization band.");
            }
            const readyImages = await Promise.all(images.map(waitImageDecode));
            if (readyImages.some(value => !value)) {
              fail("Source-bound traversal left a captured Markdown image unavailable.");
            }
            await nextFrame();
            await waitVisibleImages();
            await nextFrame();
            realizeImagesNearViewport();
            if (pendingImageSources.size !== 0) {
              fail("Source-bound terminal confirmation discovered an unrealized captured image.");
            }
            const heightAfterImages = document.documentElement.scrollHeight;
            const topAfterImages = scrollY / innerHeight;
            maxTop = Math.max(0, heightAfterImages - innerHeight) / innerHeight;
            if (topAfterImages < maxTop - positionTolerance) {
              continue;
            }
            const terminalProofFrameWaitMs =
              (await measuredNextFrame()) + (await measuredNextFrame());
            const finalHeight = document.documentElement.scrollHeight;
            const finalTop = scrollY / innerHeight;
            const finalMaxTop = Math.max(0, finalHeight - innerHeight) / innerHeight;
            if (finalHeight === heightAfterImages &&
                Math.abs(finalTop - topAfterImages) <= positionTolerance / 4 &&
                Math.abs(finalTop - finalMaxTop) <= positionTolerance) {
              auditOnlyFrameWaitMs += terminalProofFrameWaitMs;
              auditOnlyFrameWaitCount += 2;
              terminalConfirmed = true;
              break;
            }
            continue;
          }

          failureStage = "source-tail-movement";
          if (traversalViewportCount + sourceTailViewportCount >= maximumSteps) {
            fail("Source-bound Edge traversal exceeded its bounded combined viewport count.");
          }
          const nextTailOffset = nextSourceTailOffset(actualTop, maxTop, viewportStepRatio);
          if (nextTailOffset === null) {
            fail("Source-bound Edge source-tail traversal could not advance while content remained below the viewport.");
          }
          scrollTo(0, nextTailOffset * innerHeight);
          let stable = false;
          let previousHeight = -1;
          let previousTop = Number.NaN;
          let settledTop = Number.NaN;
          for (let pass = 0; pass < 8; pass++) {
            failureStage = "source-tail-settle";
            let passFrameWaitMs = await measuredNextFrame();
            realizeImagesNearViewport();
            await waitVisibleImages();
            passFrameWaitMs += await measuredNextFrame();
            realizeImagesNearViewport();
            await waitVisibleImages();
            passFrameWaitMs += await measuredNextFrame();
            settledTop = scrollY / innerHeight;
            const currentHeight = document.documentElement.scrollHeight;
            if (previousHeight >= 0 && currentHeight === previousHeight &&
                Math.abs(settledTop - previousTop) <= positionTolerance / 4) {
              auditOnlyFrameWaitMs += passFrameWaitMs;
              auditOnlyFrameWaitCount += 3;
              stable = true;
              break;
            }
            previousHeight = currentHeight;
            previousTop = settledTop;
          }
          if (!stable) {
            fail("Source-bound Edge source-tail movement did not reach a stable position.");
          }
          failureStage = "source-tail-observation";
          const tailValidation = validateNativeCaptureObservation(
            settledTop,
            nextTailOffset,
            previousTailOffset,
            positionTolerance,
            viewportStepRatio,
          );
          if (!tailValidation.positionMatches) {
            fail("Source-bound Edge source-tail capture did not match its bounded target offset.");
          }
          if (!tailValidation.stepPreservesOverlap) {
            fail("Source-bound Edge source-tail capture did not preserve bounded overlapping coverage.");
          }
          sourceTailCaptureOffsetsViewportUnits.push(settledTop);
          sourceTailViewportCount++;
          sourceTailMovementCount++;
          previousTailOffset = settledTop;
        }
        if (captureOffsetsViewportUnits.length > 1) {
          measuredViewportStepRatio = captureOffsetsViewportUnits.slice(1)
            .reduce((sum, offset, index) => sum + offset - captureOffsetsViewportUnits[index], 0) /
            (captureOffsetsViewportUnits.length - 1);
        }
      } else {
        failureStage = "overlap-traversal";
        const step = Math.max(1, innerHeight * viewportStepRatio);
        let position = 0;
        while (true) {
          if (++traversalViewportCount > maximumSteps) {
            fail("Source-bound Edge traversal exceeded its bounded viewport count.");
          }
          scrollTo(0, position);
          await nextFrame();
          realizeImagesNearViewport();
          await waitVisibleImages();
          await nextFrame();
          const actualTop = scrollY;
          captureOffsetsViewportUnits.push(actualTop / innerHeight);
          const maxTop = Math.max(0, document.documentElement.scrollHeight - innerHeight);
          if (actualTop < maxTop) {
            const nextPosition = Math.min(maxTop, actualTop + step);
            if (nextPosition <= actualTop) {
              fail("Source-bound Edge traversal could not advance while content remained below the viewport.");
            }
            movementCount++;
            position = nextPosition;
            continue;
          }

          realizeImagesNearViewport();
          if (pendingImageSources.size !== 0) {
            failureStage = "terminal-images";
            fail("Source-bound traversal reached its terminal viewport with captured images outside the realization band.");
          }
          const readyImages = await Promise.all(images.map(waitImageDecode));
          if (readyImages.some(value => !value)) {
            fail("Source-bound traversal left a captured Markdown image unavailable.");
          }
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

          const terminalProofFrameWaitMs =
            (await measuredNextFrame()) + (await measuredNextFrame());
          const finalHeight = document.documentElement.scrollHeight;
          const finalMaxTop = Math.max(0, finalHeight - innerHeight);
          failureStage = "terminal-confirmation";
          if (finalHeight !== heightAfterPaint || scrollY < finalMaxTop) {
            position = scrollY;
            continue;
          }
          auditOnlyFrameWaitMs += terminalProofFrameWaitMs;
          auditOnlyFrameWaitCount += 2;
          break;
        }
        movementCount = Math.max(0, traversalViewportCount - 1);
      }
      const fullTraversalMs = performance.now() - started;
      const chargedTraversalMs = fullTraversalMs - auditOnlyFrameWaitMs;
      if (!Number.isFinite(chargedTraversalMs) || chargedTraversalMs <= 0 ||
          chargedTraversalMs < firstViewportImagesReadyMs ||
          !Number.isFinite(auditOnlyFrameWaitMs) || auditOnlyFrameWaitMs < 0 ||
          !Number.isSafeInteger(auditOnlyFrameWaitCount) || auditOnlyFrameWaitCount < 0) {
        fail("Source-bound Edge traversal produced invalid raw or charged timing evidence.");
      }
      failureStage = "rendered-dimensions";
      const width = Math.ceil(Math.max(document.documentElement.scrollWidth, innerWidth));
      const height = Math.ceil(Math.max(document.documentElement.scrollHeight, innerHeight));
      if (!(width > 0) || !(height > 0) || width > 16384 || height > 100000000) {
        fail("Source-bound Edge article has invalid or over-budget dimensions.");
      }
      failureStage = "semantic-summary";
      const semantic = await summarizeSourceReplaySemantics({
        article,
        images,
        expectedUrlHashes,
        inlineImageDigests,
        sourceHrefByAnchor,
        readmeBaseUrl: expected.readmeBaseUrl,
        semanticDigestKey: expected.semanticDigestKey,
        digestSemanticText,
        unsupportedSemanticMarkupCount,
        createSemanticTokenDigestCounts,
        hashText,
        hashBytes,
      });
      await new Promise(resolve => setTimeout(resolve, 50));
      return {
        ok: true,
        viewport: {
          width: innerWidth,
          height: innerHeight,
          deviceScaleFactor: devicePixelRatio,
          colorScheme: expected.colorScheme,
        },
        timing: {
          firstViewportPaintMs,
          firstViewportImagesReadyMs,
          fullTraversalMs,
          chargedTraversalMs,
          auditOnlyFrameWaitMs,
          auditOnlyFrameWaitCount,
          viewportStepRatio: measuredViewportStepRatio,
          traversalViewportCount,
          movementCount,
          sourceTailViewportCount,
          sourceTailMovementCount,
          sourceTailCaptureOffsetsViewportUnits,
          viewportProfileSha256: nativeViewportProfile?.sha256 || "",
          captureOffsetsViewportUnits,
        },
        width: Math.min(width, innerWidth),
        height,
        expectedImageCount: images.length,
        verifiedImageCount: images.length,
        firstViewportRealizedImageCount,
        expectedUrlHashes: [...expectedUrlHashes],
        semanticImageAliasMatchCount,
        blockedExternalUris: [...blockedExternalUris],
        semantic,
      };
    } catch (error) {
      return {
        ok: false,
        failureStage,
        failurePredicate: ["conflicting-image-aliases", "invalid-image-dimension", "invalid-image-url", "invalid-inline-image-data",
          "inline-image-byte-mismatch", "missing-captured-image-alias", "responsive-image-candidates",
          "sanitized-image-count", "unmatched-inline-image-evidence", "unsupported-image-url-scheme"]
          .includes(error?.sourceReplayFailurePredicate) ? error.sourceReplayFailurePredicate : undefined,
        imageDecodeEvidence,
        imageMapEvidence: error?.sourceReplayImageMapEvidence &&
          error?.sourceReplayFailurePredicate === "missing-captured-image-alias"
          ? error.sourceReplayImageMapEvidence
          : undefined,
        errorType: ["AbortError", "DOMException", "Error", "RangeError", "ReferenceError", "SyntaxError", "TimeoutError", "TypeError"]
          .includes(error?.name) ? error.name : "Error",
        error: String(error?.message || error).replace(/https?:\/\/[^\s"'<>]+/giu, "[redacted-url]"),
      };
    } finally {
      if (!retainInlineObjectUrls) {
        for (const url of inlineObjectUrls) URL.revokeObjectURL(url);
      }
      removeEventListener("securitypolicyviolation", onPolicyViolation);
    }

    async function summarizeSourceReplaySemantics({
      article,
      images,
      expectedUrlHashes,
      inlineImageDigests,
      sourceHrefByAnchor,
      readmeBaseUrl,
      semanticDigestKey,
      digestSemanticText,
      unsupportedSemanticMarkupCount,
      createSemanticTokenDigestCounts,
      hashText,
      hashBytes,
    }) {
      const clean = value => String(value || "").replace(/\s+/gu, " ").trim();
      const isRendered = node => {
        for (let ancestor = node.parentElement; ancestor && ancestor !== article; ancestor = ancestor.parentElement) {
          if (ancestor.tagName !== "DETAILS" || ancestor.hasAttribute("open")) continue;
          const summary = ancestor.querySelector(":scope > summary");
          if (!summary?.contains(node)) return false;
        }
        const style = getComputedStyle(node);
        return style.display !== "none" && style.visibility !== "hidden" && Number(style.opacity) !== 0 &&
          [...node.getClientRects()].some(bounds => bounds.width > 0 && bounds.height > 0);
      };
      const visibleText = article.innerText || article.textContent || "";
      const visibleTextTokenDigests = await createSemanticTokenDigestCounts(
        visibleText, digestSemanticText);
      const mermaidSources = [...article.querySelectorAll("pre")]
        .filter(isRendered)
        .filter(pre => pre.querySelector("code.language-mermaid,code.lang-mermaid") ||
          pre.matches('pre[lang="mermaid"],pre[data-language="mermaid"]'))
        .map(pre => (pre.innerText || pre.textContent || "")
          .replace(/\r\n/gu, "\n").replace(/\r/gu, "\n").trim())
        .filter(Boolean);
      const visibleMermaidSourceDigests = await Promise.all(mermaidSources.map(digestSemanticText));
      const distinctLinks = new Set();
      let invalidLinkCount = 0;
      for (const link of [...article.querySelectorAll("a")].filter(isRendered)) {
        const href = sourceHrefByAnchor.get(link);
        if (typeof href !== "string") continue;
        let destination;
        try {
          destination = new URL(href, readmeBaseUrl).href;
        } catch {
          invalidLinkCount++;
          continue;
        }
        if (clean(link.innerText) || !destination.includes("#")) {
          distinctLinks.add(destination.toLowerCase());
        }
      }
      const visibleImages = images.filter(isRendered);
      const unsupportedMediaCount = article.querySelectorAll("audio,video").length;
      const rect = article.getBoundingClientRect();
      const visibleTextTokenCount = Object.values(visibleTextTokenDigests)
        .reduce((sum, count) => sum + count, 0);
      const complete = unsupportedSemanticMarkupCount === 0 && unsupportedMediaCount === 0 &&
        invalidLinkCount === 0 && visibleImages.length === images.length;
      const keyBytes = Uint8Array.from(
        semanticDigestKey.match(/.{2}/gu).map(value => Number.parseInt(value, 16)));
      return {
        complete,
        incompleteReason: complete ? "" : "unsupported-or-unrendered-semantic-content",
        tokenizationVersion: "rune-l-n-mn-mc-han-nfc-simple-lower-invariant-v1",
        digestKeySha256: await hashBytes(keyBytes),
        visibleTextTokenCount,
        visibleTextTokenDigests,
        width: rect.width,
        height: rect.height,
        headingCount: [...article.querySelectorAll("h1,h2,h3,h4,h5,h6")].filter(isRendered).length,
        distinctLinkCount: distinctLinks.size,
        imageCount: visibleImages.length,
        distinctImageCount: new Set([...expectedUrlHashes, ...inlineImageDigests]).size,
        mediaCount: unsupportedMediaCount,
        distinctMediaCount: unsupportedMediaCount,
        tableCount: [...article.querySelectorAll("table")].filter(isRendered).length,
        codeBlockCount: [...article.querySelectorAll("pre")].filter(isRendered).length,
        taskCheckboxCount: [...article.querySelectorAll('input[type="checkbox"]')].filter(isRendered).length,
        detailsCount: [...article.querySelectorAll("details")]
          .filter(isRendered)
          .filter(node => !node.classList.contains("details-reset")).length,
        visibleMermaidSourceDigests,
      };
    }

    function decodeInlineImageDataUri(value) {
      if (typeof value !== "string" || new TextEncoder().encode(value).byteLength > ${MAX_INLINE_IMAGE_URI_BYTES}) {
        fail("Markdown inline image URL exceeds the bounded byte limit.", "invalid-inline-image-data");
      }
      const match = /^data:(image\/[a-z0-9.+-]+)(?:;charset=utf-8)?(;base64)?,([\s\S]*)$/iu.exec(value);
      const mimeType = match?.[1]?.toLowerCase();
      if (!match || !${JSON.stringify([...SOURCE_IMAGE_MIMES])}.includes(mimeType)) {
        fail("Markdown inline image has an unsupported data URI format.", "invalid-inline-image-data");
      }
      const payload = match[3];
      let bytes;
      if (match[2]) {
        if (!payload || !/^(?:[a-z0-9+/]{4})*(?:[a-z0-9+/]{2}(?:==)?|[a-z0-9+/]{3}=?)?$/iu.test(payload)) {
          fail("Markdown inline image contains malformed base64 data.", "invalid-inline-image-data");
        }
        let binary;
        try { binary = atob(payload); } catch { fail("Markdown inline image contains malformed base64 data.", "invalid-inline-image-data"); }
        if (binary.length > ${MAX_INLINE_IMAGE_URI_BYTES}) {
          fail("Markdown inline image exceeds the decoded byte limit.", "invalid-inline-image-data");
        }
        bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
      } else {
        if (mimeType !== "image/svg+xml") {
          fail("Non-base64 inline image data is supported only for UTF-8 SVG.", "invalid-inline-image-data");
        }
        const encoded = new TextEncoder().encode(payload);
        const decoded = new Uint8Array(encoded.length);
        let written = 0;
        for (let index = 0; index < encoded.length;) {
          if (encoded[index] === 0x25) {
            if (index + 2 >= encoded.length) fail("Markdown inline SVG contains an incomplete percent escape.", "invalid-inline-image-data");
            const high = hexValue(encoded[index + 1]);
            const low = hexValue(encoded[index + 2]);
            if (high < 0 || low < 0) fail("Markdown inline SVG contains an invalid percent escape.", "invalid-inline-image-data");
            decoded[written++] = (high << 4) | low;
            index += 3;
          } else {
            decoded[written++] = encoded[index++];
          }
        }
        if (written > ${MAX_INLINE_IMAGE_URI_BYTES}) fail("Markdown inline SVG exceeds the decoded byte limit.", "invalid-inline-image-data");
        bytes = decoded.subarray(0, written);
      }
      return { mimeType, bytes };
    }

    function hexValue(value) {
      if (value >= 0x30 && value <= 0x39) return value - 0x30;
      if (value >= 0x41 && value <= 0x46) return value - 0x41 + 10;
      if (value >= 0x61 && value <= 0x66) return value - 0x61 + 10;
      return -1;
    }
  }`;
}

async function waitForExpression(cdp, expression, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const result = await cdp.send("Runtime.evaluate", { expression, returnByValue: true });
      if (!result.exceptionDetails && result.result?.value) return;
    } catch {
      // Navigation briefly replaces the JavaScript execution context.
    }
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error("Timed out waiting for the source-bound loopback page.");
}

async function launchSourceReplay({ edgePath, profileDirectory, cdp }) {
  const edge = spawn(edgePath, [
    "--headless=new",
    "--no-first-run",
    "--no-default-browser-check",
    "--disable-background-networking",
    "--disable-component-update",
    "--disable-sync",
    "--metrics-recording-only",
    "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1",
    "--remote-debugging-port=0",
    `--user-data-dir=${profileDirectory}`,
    "about:blank",
  ], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
  let edgeError = "";
  edge.stderr.setEncoding("utf8");
  edge.stderr.on("data", chunk => { edgeError = (edgeError + chunk).slice(-8192); });
  const portFile = path.join(profileDirectory, "DevToolsActivePort");
  const portContent = await waitForDevToolsPort(portFile, edge, () => edgeError, 20_000);
  const port = Number(portContent.split(/\r?\n/u, 1)[0]);
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`, {
    signal: AbortSignal.timeout(10_000),
  })).json();
  if (!Array.isArray(targets)) throw new Error("Edge returned an invalid DevTools target list.");
  const target = targets.find(item => item.type === "page");
  if (!target?.webSocketDebuggerUrl) throw new Error("Edge did not expose a source-bound page WebSocket.");
  const connection = await connectCdp(target.webSocketDebuggerUrl);
  await Promise.all([
    connection.send("Page.enable"),
    connection.send("Runtime.enable"),
    connection.send("Network.enable"),
  ]);
  return { edge, cdp: connection };
}

function parseArguments(args) {
  const result = {};
  for (const argument of args) {
    const match = /^--([^=]+)=(.*)$/u.exec(argument);
    if (match) result[match[1]] = match[2];
  }
  return result;
}

function readPositiveInteger(options, key, fallback) {
  const value = options[key] === undefined ? fallback : Number(options[key]);
  if (!Number.isSafeInteger(value) || value <= 0) throw new Error(`--${key} must be a positive integer.`);
  return value;
}

function readPositiveNumber(options, key) {
  const value = Number(options[key]);
  if (!Number.isFinite(value) || value <= 0 || value > 8) {
    throw new Error(`--${key} must be a positive finite number no greater than 8.`);
  }
  return value;
}

function findDefaultEdge() {
  return [
    path.join(process.env["ProgramFiles(x86)"] || "", "Microsoft", "Edge", "Application", "msedge.exe"),
    path.join(process.env.ProgramFiles || "", "Microsoft", "Edge", "Application", "msedge.exe"),
  ].find(candidate => candidate && existsSync(candidate));
}

function redactUrls(value) {
  return String(value).replace(/https?:\/\/[^\s"'<>]+/giu, match =>
    `sha256:${createHash("sha256").update(match, "utf8").digest("hex")}`);
}

async function runCli() {
  const options = parseArguments(process.argv.slice(2));
  const semanticDigestKey = process.env.JITHUB_README_AUDIT_SEMANTIC_HMAC_KEY || "";
  delete process.env.JITHUB_README_AUDIT_SEMANTIC_HMAC_KEY;
  const corpusDirectory = options.corpus;
  const outputDirectory = options.out ? path.resolve(options.out) : "";
  const reportPath = options.report ? path.resolve(options.report) :
    outputDirectory ? path.join(outputDirectory, "same-byte-source-replay.json") : "";
  const width = readPositiveInteger(options, "width");
  const height = readPositiveInteger(options, "height");
  const deviceScaleFactor = readPositiveNumber(options, "device-scale-factor");
  const maximumTiles = readPositiveInteger(options, "max-tiles", DEFAULT_MAXIMUM_TILES);
  const nativeViewportProfilePath = options["native-viewport-profile"] || "";
  const viewportStepRatio = options["viewport-step-ratio"] === undefined
    ? SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO
    : readPositiveNumber(options, "viewport-step-ratio");
  const colorScheme = options["color-scheme"] || "light";
  const edgePath = options.edge || findDefaultEdge();
  if (!corpusDirectory || !outputDirectory || !reportPath || !edgePath) {
    throw new Error("Usage: node same-byte-edge-source.mjs --corpus=<case-corpus> --out=<output-dir> --width=<native-content-width-dip> --height=<native-content-height-dip> --device-scale-factor=<native-dpi-over-96> [--native-viewport-profile=<json-path>] [--color-scheme=light|dark] [--report=<path>] [--max-tiles=512] [--edge=<msedge.exe>]");
  }
  if (!["light", "dark"].includes(colorScheme)) {
    throw new Error("--color-scheme must be either light or dark.");
  }
  if (viewportStepRatio >= 1) {
    throw new Error("--viewport-step-ratio must be less than one viewport height.");
  }
  await mkdir(outputDirectory, { recursive: true });
  const replayServer = await createSameByteReplayServer(corpusDirectory);
  const profileDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-readme-source-edge-"));
  let edge;
  let cdp;
  try {
    if (replayServer.manifest?.browserRender?.status !== "passed") {
      throw new Error("Source-bound Edge replay requires a captured GitHub-rendered article.");
    }
    const launched = await launchSourceReplay({ edgePath, profileDirectory });
    edge = launched.edge;
    cdp = launched.cdp;
    const nativeViewportProfile = await loadNativeViewportProfile(nativeViewportProfilePath, height);
    const report = await replaySourceBoundMarkdownInEdge({
      cdp,
      replayServer,
      outputDirectory,
      viewport: { width, height, deviceScaleFactor },
      colorScheme,
      semanticDigestKey,
      maximumTiles,
      viewportStepRatio,
      nativeViewportProfile,
    });
    await writeFile(reportPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");
    process.stdout.write(`${JSON.stringify({ ok: true, report: reportPath, status: report.status })}\n`);
  } catch (error) {
    const report = createSourceReplayFailureReport(error);
    await writeFile(reportPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");
    process.stderr.write(`${JSON.stringify({
      ok: false,
      status: report.status,
      failureCategory: report.failureCategory,
      failureStage: report.failureStage,
      errorType: report.errorType,
      error: report.error,
    })}\n`);
    process.exitCode = 1;
  } finally {
    // Browser.close may terminate the socket before acknowledging the command.
    // The profile-specific process cleanup below remains authoritative.
    try {
      if (cdp) await Promise.race([
        cdp.send("Browser.close"),
        new Promise(resolve => setTimeout(resolve, 1000)),
      ]);
    } catch {}
    try { cdp?.close(); } catch {}
    try { edge?.kill(); } catch {}
    try { await stopBrowserProfileProcesses(profileDirectory); } catch {}
    try { await rm(profileDirectory, { recursive: true, force: true }); } catch {}
    await replayServer.close();
  }
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) {
  await runCli();
}
