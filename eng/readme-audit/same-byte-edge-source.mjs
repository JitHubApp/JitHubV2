import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { lstat, mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { waitForDevToolsPort } from "./browser-launch.mjs";
import { connectCdp } from "./cdp-client.mjs";
import { cleanupBrowserProfile } from "./browser-profile-cleanup.mjs";
import {
  createCapturedImageAltIdentities,
  createSameByteReplayServer,
  PINNED_GITHUB_README_STYLE_PROFILE,
  PINNED_SOURCE_REPLAY_PARSER,
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
  "rendered-html-response",
  "rendered-html-digest",
  "style-profile",
  "asset-map",
  "readme-response",
  "readme-digest",
  "parser-pin",
  "emoji-shortcode-map",
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
  "native-source-identity",
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
  "captured-image-route-mismatch",
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
       requestUrl.pathname === PINNED_GITHUB_README_STYLE_PROFILE.cssPath ||
       requestUrl.pathname === "/marked-parser.js" ||
       requestUrl.pathname === "/github-emoji-shortcodes.js" ||
       requestUrl.pathname === "/github-emoji-shortcodes.json" ||
       requestUrl.pathname === "/manifest" ||
       requestUrl.pathname === "/readme" ||
       requestUrl.pathname === "/rendered-html" ||
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
    await waitForExpression(cdp, "document.readyState === 'complete' && typeof marked === 'object' && typeof installPinnedGitHubEmojiShortcodes === 'function'", 15_000);

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
          renderedHtmlSha256: replayServer.renderedHtmlSha256,
          renderedHtmlByteSize: replayServer.renderedHtmlBytes.length,
          manifestSha256: replayServer.manifestSha256,
          assetUrlMapSha256: replayServer.assetUrlMapSha256,
          capturedInlineImageEvidence,
          capturedImageAltIdentities,
          capturedImageRouteCount: replayServer.manifest.imageRoutes.length,
          semanticDigestKey,
          parser: { ...PINNED_SOURCE_REPLAY_PARSER },
          styleProfile: {
            ...PINNED_GITHUB_README_STYLE_PROFILE,
            integrity: `sha256-${Buffer.from(PINNED_GITHUB_README_STYLE_PROFILE.sha256, "hex").toString("base64")}`,
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
        Object.defineProperty(error, "sourceReplayNativeMovementEvidence", {
          value: sanitizeSourceReplayNativeMovementEvidence(replay?.nativeMovementEvidence),
          configurable: true,
        });
        Object.defineProperty(error, "sourceReplayTerminalClampObservations", {
          value: sanitizeSourceReplayTerminalClampObservations(replay?.terminalClampObservations),
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

    if (nativeViewportProfile) {
      failureStage = "native-terminal-profile-bottom";
      if (!isSourceReplayExtentCompatible(
        nativeViewportProfile.nativeEstimatedContentHeightPx,
        replay.height,
        viewport.height)) {
        const error = new Error(
          "Source-bound native and Edge full-page extents are outside the 0.90–1.10 fidelity envelope.");
        error.sourceReplayExtentEvidence = {
          nativeEstimatedContentHeightPx: nativeViewportProfile.nativeEstimatedContentHeightPx,
          edgeRenderedContentHeightPx: replay.height,
          viewportHeightPx: viewport.height,
          styleProfileId: PINNED_GITHUB_README_STYLE_PROFILE.id,
          styleProfileVersion: PINNED_GITHUB_README_STYLE_PROFILE.version,
          styleProfileSha256: PINNED_GITHUB_README_STYLE_PROFILE.sha256,
        };
        throw error;
      }
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
    // Edge may request the integrity-pinned script twice during a navigation.
    // Every request is served from the same verified vendored bytes; require
    // successful loading, not an incidental exact HTTP request count.
    if (replayServer.misses !== 0 ||
        replayServer.servedParserCount < 1 ||
        replayServer.servedEmojiExtensionCount < 1 ||
        replayServer.servedEmojiMapCount < 1 ||
        replayServer.servedStyleProfileCount < 1 ||
        replay.verifiedImageCount !== replay.expectedImageCount ||
        expectedUrlHashes.length !== servedUrlHashes.length ||
        expectedUrlHashes.some((value, index) => value !== servedUrlHashes[index])) {
      throw new Error(
        "Source-bound Edge replay did not verify every parser, image asset, and mapped URL hash exactly. " +
        `misses=${replayServer.misses} parsers=${replayServer.servedParserCount} ` +
        `emojiExtension=${replayServer.servedEmojiExtensionCount} emojiMap=${replayServer.servedEmojiMapCount} ` +
        `styleProfile=${replayServer.servedStyleProfileCount} ` +
        `images=${replay.verifiedImageCount}/${replay.expectedImageCount} ` +
        `urlHashes=${servedUrlHashes.length}/${expectedUrlHashes.length} ` +
        `hashSetsEqual=${expectedUrlHashes.every((value, index) => value === servedUrlHashes[index])}.`);
    }

    return {
      schemaVersion: 3,
      status: "passed",
      source: {
        readmeGitBlobSha1: replayServer.readmeGitBlobSha1,
        readmeSha256: replayServer.readmeSha256,
        byteSize: replayServer.readmeBytes.length,
        decodedTextSha256: replay.decodedTextSha256,
      },
      parser: { ...PINNED_SOURCE_REPLAY_PARSER },
      styleProfile: {
        id: PINNED_GITHUB_README_STYLE_PROFILE.id,
        version: PINNED_GITHUB_README_STYLE_PROFILE.version,
        sha256: PINNED_GITHUB_README_STYLE_PROFILE.sha256,
        colorScheme: PINNED_GITHUB_README_STYLE_PROFILE.colorScheme,
        edgeStylesheetLoaded: replay.styleProfileEvidence?.loaded === true,
        edgeIntegrityAttributeMatched: replay.styleProfileEvidence?.integrityAttributeMatched === true,
        loopbackResponseCount: replayServer.servedStyleProfileCount,
      },
      viewport: {
        width: viewport.width,
        height: viewport.height,
        deviceScaleFactor: viewport.deviceScaleFactor,
        colorScheme: replay.viewport.colorScheme,
        edgeInnerWidth: replay.viewport.width,
        edgeInnerHeight: replay.viewport.height,
        edgeDeviceScaleFactor: replay.viewport.deviceScaleFactor,
        documentClientWidth: replay.viewport.documentClientWidth,
        bodyClientWidth: replay.viewport.bodyClientWidth,
        articleWidth: replay.viewport.articleWidth,
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
        terminalClampObservations: replay.timing.terminalClampObservations,
        viewportProfileSha256: replay.timing.viewportProfileSha256,
        captureOffsetsViewportUnits: replay.timing.captureOffsetsViewportUnits,
        firstViewportBoundary: "decoded text SHA-256 verified against native source identity; source-available start through pinned Marked GFM parse, sanitized insertion, and two requestAnimationFrames",
        firstViewportImagesReadyBoundary: "same source-available start through successful decode of every visible pinned image",
        fullTraversalBoundary: "same source-available start through bottom traversal, visible-image decode at each step, and final paint, including stability and terminal proof rAF waits",
        chargedTraversalBoundary: "same source-available start through parse, insertion, first paint, each movement's first target-matching sample, all image decode work, and source-tail capture; subtracts only measured rAF wait time from successful post-target stability confirmations and final terminal proof frames",
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
      colorScheme !== PINNED_GITHUB_README_STYLE_PROFILE.colorScheme ||
      !isPinnedGitHubReadmeStyleProfile(replayServer?.styleProfile) ||
      !Number.isSafeInteger(maximumTiles) || maximumTiles <= 0 || maximumTiles > DEFAULT_MAXIMUM_TILES ||
      !Number.isFinite(viewportStepRatio) || viewportStepRatio <= 0 || viewportStepRatio >= 1 ||
      (nativeViewportProfile !== null && !validateNativeViewportProfile(
        nativeViewportProfile,
        viewport.height,
        {
          readmeGitBlobSha1: replayServer?.readmeGitBlobSha1,
          readmeSha256: replayServer?.readmeSha256,
          byteSize: replayServer?.readmeBytes?.length,
        }))) {
    throw new Error("Source-bound Edge replay requires a validated corpus, native viewport, pinned Light style profile, semantic digest key, and bounded output target.");
  }
  if (!isPinnedSourceReplayParser(replayServer.parser)) {
    throw new Error("Source-bound Edge replay parser or GitHub emoji provenance does not match the pinned vendored inputs.");
  }
}

function isPinnedGitHubReadmeStyleProfile(profile) {
  return profile && typeof profile === "object" && !Array.isArray(profile) &&
    profile.id === PINNED_GITHUB_README_STYLE_PROFILE.id &&
    profile.version === PINNED_GITHUB_README_STYLE_PROFILE.version &&
    profile.colorScheme === PINNED_GITHUB_README_STYLE_PROFILE.colorScheme &&
    profile.cssPath === PINNED_GITHUB_README_STYLE_PROFILE.cssPath &&
    profile.sha256 === PINNED_GITHUB_README_STYLE_PROFILE.sha256;
}

function isPinnedSourceReplayParser(parser) {
  if (!parser || typeof parser !== "object" || Array.isArray(parser)) return false;
  const fields = Object.keys(PINNED_SOURCE_REPLAY_PARSER);
  const actualFields = Object.keys(parser);
  return actualFields.length === fields.length &&
    fields.every(field => parser[field] === PINNED_SOURCE_REPLAY_PARSER[field]);
}

export function createUniqueCapturedImageAltRouteMap(sourceAltSha256s, capturedImageAltIdentities, capturedRouteCount) {
  if (!Array.isArray(sourceAltSha256s) || !Array.isArray(capturedImageAltIdentities) ||
      !Number.isSafeInteger(capturedRouteCount) || capturedRouteCount <= 0 ||
      sourceAltSha256s.length !== capturedRouteCount ||
      capturedImageAltIdentities.length !== capturedRouteCount) {
    return null;
  }
  const sourceAltCounts = new Map();
  for (const hash of sourceAltSha256s) {
    if (hash === null) continue;
    if (!/^[0-9a-f]{64}$/iu.test(hash || "")) return null;
    const normalized = hash.toLowerCase();
    sourceAltCounts.set(normalized, (sourceAltCounts.get(normalized) || 0) + 1);
  }
  const capturedIndexByAltHash = new Map();
  const capturedAltCounts = new Map();
  const capturedIndexes = new Set();
  for (const identity of capturedImageAltIdentities) {
    const hash = identity?.altSha256;
    if (!Number.isSafeInteger(identity?.index) || identity.index < 0 ||
        !Object.hasOwn(identity, "altSha256") ||
        capturedIndexes.has(identity.index)) {
      return null;
    }
    capturedIndexes.add(identity.index);
    if (hash === null) continue;
    if (!/^[0-9a-f]{64}$/iu.test(hash || "")) return null;
    const normalized = hash.toLowerCase();
    capturedAltCounts.set(normalized, (capturedAltCounts.get(normalized) || 0) + 1);
    capturedIndexByAltHash.set(normalized, identity.index);
  }
  // An empty/duplicated badge alt cannot identify its own payload, but it
  // must not suppress a different image's exact, unique alt identity. Never
  // infer an alias from ordinal position or a nonmatching alt.
  const mapping = new Map();
  for (const [hash, count] of sourceAltCounts) {
    if (count === 1 && capturedAltCounts.get(hash) === 1)
      mapping.set(hash, capturedIndexByAltHash.get(hash));
  }
  return mapping.size > 0 ? mapping : null;
}

/**
 * Creates a one-to-one matcher between Markdown images and the image routes
 * captured from GitHub's rendered article. URL hashes are preferred; a unique
 * alt identity can bridge a rewritten URL, and a payload identity is the last
 * safe alias fallback. The caller must still provide the selected bytes' SHA
 * and MIME, so no route is claimed on URL or ordinal position alone.
 */
export function createCapturedImageRouteIdentityMatcher(imageRoutes) {
  if (!Array.isArray(imageRoutes) || imageRoutes.length > 15_000) return null;
  const supportedMimeTypes = new Set([
    "image/avif", "image/bmp", "image/gif", "image/jpeg", "image/png",
    "image/svg+xml", "image/webp", "image/x-icon",
  ]);
  const byIndex = new Map();
  const byUrlHash = new Map();
  for (const route of imageRoutes) {
    if (!route || !Number.isSafeInteger(route.index) || route.index < 0 || route.index >= 15_000 ||
        byIndex.has(route.index)) return null;
    const isDataUri = route.dataUri === true;
    if (isDataUri) {
      if (Object.hasOwn(route, "urlSha256") || Object.hasOwn(route, "sha256") ||
          Object.hasOwn(route, "mimeType")) return null;
    } else if (typeof route.urlSha256 !== "string" || !/^[0-9a-f]{64}$/iu.test(route.urlSha256) ||
        typeof route.sha256 !== "string" || !/^[0-9a-f]{64}$/iu.test(route.sha256) ||
        typeof route.mimeType !== "string" || !supportedMimeTypes.has(route.mimeType.toLowerCase())) {
      return null;
    }
    byIndex.set(route.index, route);
    if (!isDataUri) {
      const hash = route.urlSha256.toLowerCase();
      const matching = byUrlHash.get(hash) || [];
      matching.push(route);
      byUrlHash.set(hash, matching);
    }
  }

  const claimedIndexes = new Set();
  const claim = ({ candidateUrlSha256s = [], routeIndex = null, sha256, mimeType, dataUri = false } = {}) => {
    if (typeof dataUri !== "boolean" || typeof sha256 !== "string" || !/^[0-9a-f]{64}$/iu.test(sha256) ||
        typeof mimeType !== "string" || !supportedMimeTypes.has(mimeType.toLowerCase())) return null;
    const normalizedPayload = sha256.toLowerCase();
    const normalizedMime = mimeType.toLowerCase();
    const isCompatible = route => route && !claimedIndexes.has(route.index) &&
      ((route.dataUri === true) === dataUri) && (dataUri ||
        (route.sha256.toLowerCase() === normalizedPayload && route.mimeType.toLowerCase() === normalizedMime));

    if (dataUri) {
      const route = byIndex.get(routeIndex);
      if (!isCompatible(route)) return null;
      claimedIndexes.add(route.index);
      return route.index;
    }

    if (!Array.isArray(candidateUrlSha256s) || candidateUrlSha256s.length > 16 ||
        candidateUrlSha256s.some(hash => typeof hash !== "string" || !/^[0-9a-f]{64}$/iu.test(hash))) return null;
    const exactByIndex = new Map();
    for (const hash of candidateUrlSha256s) {
      for (const route of byUrlHash.get(hash.toLowerCase()) || []) exactByIndex.set(route.index, route);
    }
    const exactRoutes = [...exactByIndex.values()];

    if (Number.isSafeInteger(routeIndex)) {
      const identifiedRoute = byIndex.get(routeIndex);
      if (!isCompatible(identifiedRoute) || (exactRoutes.length > 0 && !exactByIndex.has(routeIndex))) return null;
      claimedIndexes.add(identifiedRoute.index);
      return identifiedRoute.index;
    }

    if (exactRoutes.length > 0) {
      const route = exactRoutes.find(isCompatible);
      if (!route) return null;
      claimedIndexes.add(route.index);
      return route.index;
    }

    // URL aliases can be absent from imageRoutes when GitHub rewrites authored
    // sources. Payload equality is the safe bridge; identical routes are
    // distinguishable only by their multiplicity.
    const route = [...byIndex.values()].find(isCompatible);
    if (!route) return null;
    claimedIndexes.add(route.index);
    return route.index;
  };

  return {
    routeCount: imageRoutes.length,
    claim,
    get claimedCount() { return claimedIndexes.size; },
    isComplete: () => claimedIndexes.size === imageRoutes.length,
  };
}

export function createUniqueCapturedDecorativeImageExclusionMap(sourceImageCandidates, exclusionProofs) {
  if (!Array.isArray(sourceImageCandidates) || !Array.isArray(exclusionProofs) ||
      exclusionProofs.length > 128 || sourceImageCandidates.length > 15_000) {
    return null;
  }
  if (exclusionProofs.length === 0) return new Set();

  const normalizedProofs = [];
  for (const proof of exclusionProofs) {
    if (!proof || proof.explicitEmptyAlt !== true || proof.failedDecode !== true || proof.zeroBounds !== true ||
        !Array.isArray(proof.urlSha256s) || proof.urlSha256s.length < 1 || proof.urlSha256s.length > 16) {
      return null;
    }
    const hashes = new Set();
    for (const hash of proof.urlSha256s) {
      if (!/^[0-9a-f]{64}$/iu.test(hash || "") || hashes.has(hash.toLowerCase())) return null;
      hashes.add(hash.toLowerCase());
    }
    normalizedProofs.push(hashes);
  }

  const usedSourceIndexes = new Set();
  for (const hashes of normalizedProofs) {
    const matchingSourceIndexes = [];
    for (let index = 0; index < sourceImageCandidates.length; index++) {
      const source = sourceImageCandidates[index];
      if (!source || typeof source.explicitEmptyAlt !== "boolean" || !Array.isArray(source.candidateUrlSha256s)) {
        return null;
      }
      if (!source.explicitEmptyAlt) continue;
      const sourceHashes = new Set();
      for (const hash of source.candidateUrlSha256s) {
        if (!/^[0-9a-f]{64}$/iu.test(hash || "")) return null;
        sourceHashes.add(hash.toLowerCase());
      }
      if ([...hashes].some(hash => sourceHashes.has(hash))) matchingSourceIndexes.push(index);
    }
    // URL hashes plus an explicit empty alt are the sole source identity. If
    // either side is ambiguous, retain every image and fail closed downstream.
    if (matchingSourceIndexes.length !== 1 || usedSourceIndexes.has(matchingSourceIndexes[0])) return null;
    usedSourceIndexes.add(matchingSourceIndexes[0]);
  }
  return usedSourceIndexes;
}

export function validateNativeViewportProfile(profile, viewportHeight, expectedReadmeSource = null) {
  if (!profile || profile.schemaVersion !== 1 || profile.viewportHeight !== viewportHeight ||
      !Array.isArray(profile.viewports) || profile.viewports.length < 1 ||
      profile.viewports.length > MAX_REPLAY_STEPS) {
    return false;
  }
  if (expectedReadmeSource !== null &&
      (!/^[0-9a-f]{40}$/iu.test(profile.readmeGitBlobSha1 || "") ||
       !/^[0-9a-f]{64}$/iu.test(profile.readmeSha256 || "") ||
       !/^[0-9a-f]{64}$/iu.test(profile.decodedTextSha256 || "") ||
       !Number.isSafeInteger(profile.readmeByteSize) || profile.readmeByteSize <= 0 ||
       !Number.isSafeInteger(profile.nativeEstimatedContentHeightPx) ||
       profile.nativeEstimatedContentHeightPx < viewportHeight ||
       !/^[0-9a-f]{40}$/iu.test(expectedReadmeSource.readmeGitBlobSha1 || "") ||
       !/^[0-9a-f]{64}$/iu.test(expectedReadmeSource.readmeSha256 || "") ||
       !Number.isSafeInteger(expectedReadmeSource.byteSize) || expectedReadmeSource.byteSize <= 0 ||
       profile.readmeGitBlobSha1.toLowerCase() !== expectedReadmeSource.readmeGitBlobSha1.toLowerCase() ||
       profile.readmeSha256.toLowerCase() !== expectedReadmeSource.readmeSha256.toLowerCase() ||
       profile.readmeByteSize !== expectedReadmeSource.byteSize)) {
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

export function isSourceReplayExtentCompatible(
  nativeEstimatedHeightPx, edgeRenderedHeightPx, viewportHeightPx) {
  if (!Number.isSafeInteger(nativeEstimatedHeightPx) ||
      !Number.isSafeInteger(edgeRenderedHeightPx) ||
      !Number.isSafeInteger(viewportHeightPx) || viewportHeightPx < 1 ||
      nativeEstimatedHeightPx < viewportHeightPx || edgeRenderedHeightPx < viewportHeightPx) {
    return false;
  }
  const nativeToEdgeExtentRatio = nativeEstimatedHeightPx / edgeRenderedHeightPx;
  return Number.isFinite(nativeToEdgeExtentRatio) &&
    nativeToEdgeExtentRatio >= 0.90 && nativeToEdgeExtentRatio <= 1.10;
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

async function loadNativeViewportProfile(profilePath, viewportHeight, replayServer) {
  if (!profilePath) return null;
  const resolvedPath = path.resolve(profilePath);
  const fileInfo = await lstat(resolvedPath);
  if (!fileInfo.isFile() || fileInfo.isSymbolicLink() || fileInfo.size <= 0 || fileInfo.size > 4 * 1024 * 1024) {
    throw new Error("Native viewport profile is missing, linked, or over its bounded size.");
  }
  const bytes = await readFile(resolvedPath);
  const profile = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  if (!validateNativeViewportProfile(profile, viewportHeight, {
    readmeGitBlobSha1: replayServer?.readmeGitBlobSha1,
    readmeSha256: replayServer?.readmeSha256,
    byteSize: replayServer?.readmeBytes?.length,
  })) {
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
    return { index: route.index, mimeType: decoded.mimeType, sha256: sha256(decoded.bytes) };
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
  const sourceRouteIndex = Number.isSafeInteger(value.sourceRouteIndex) &&
    value.sourceRouteIndex >= 0 && value.sourceRouteIndex < 15_000
    ? value.sourceRouteIndex
    : null;
  const routeCandidates = [];
  if (Array.isArray(value.routeCandidates)) {
    const seenRouteIndexes = new Set();
    for (const candidate of value.routeCandidates.slice(0, 8)) {
      if (!candidate || !Number.isSafeInteger(candidate.routeIndex) || candidate.routeIndex < 0 ||
          candidate.routeIndex >= 15_000 || seenRouteIndexes.has(candidate.routeIndex) ||
          !/^[0-9a-f]{64}$/iu.test(candidate.urlSha256 || "")) {
        continue;
      }
      const route = {
        routeIndex: candidate.routeIndex,
        urlSha256: candidate.urlSha256.toLowerCase(),
      };
      if (/^[0-9a-f]{64}$/iu.test(candidate.payloadSha256 || "") &&
          SOURCE_IMAGE_MIMES.has(candidate.mimeType)) {
        route.payloadSha256 = candidate.payloadSha256.toLowerCase();
        route.mimeType = candidate.mimeType;
      }
      routeCandidates.push(route);
      seenRouteIndexes.add(candidate.routeIndex);
    }
  }
  return {
    imageIndex: value.imageIndex,
    candidateUrlSha256s,
    capturedAssetUrlEntryCount: value.capturedAssetUrlEntryCount,
    sourceRouteIndex,
    routeCandidates,
  };
}

function sanitizeSourceReplayExtentEvidence(value) {
  if (!value || typeof value !== "object" || Array.isArray(value) ||
      !Number.isSafeInteger(value.nativeEstimatedContentHeightPx) ||
      value.nativeEstimatedContentHeightPx < 1 || value.nativeEstimatedContentHeightPx > 100_000_000 ||
      !Number.isSafeInteger(value.edgeRenderedContentHeightPx) ||
      value.edgeRenderedContentHeightPx < 1 || value.edgeRenderedContentHeightPx > 100_000_000 ||
      !Number.isSafeInteger(value.viewportHeightPx) || value.viewportHeightPx < 1 || value.viewportHeightPx > 8192 ||
      value.styleProfileId !== PINNED_GITHUB_README_STYLE_PROFILE.id ||
      value.styleProfileVersion !== PINNED_GITHUB_README_STYLE_PROFILE.version ||
      value.styleProfileSha256 !== PINNED_GITHUB_README_STYLE_PROFILE.sha256) {
    return null;
  }
  return {
    nativeEstimatedContentHeightPx: value.nativeEstimatedContentHeightPx,
    edgeRenderedContentHeightPx: value.edgeRenderedContentHeightPx,
    viewportHeightPx: value.viewportHeightPx,
    nativeToEdgeExtentRatio: Number((value.nativeEstimatedContentHeightPx /
      value.edgeRenderedContentHeightPx).toFixed(6)),
    styleProfileId: PINNED_GITHUB_README_STYLE_PROFILE.id,
    styleProfileVersion: PINNED_GITHUB_README_STYLE_PROFILE.version,
    styleProfileSha256: PINNED_GITHUB_README_STYLE_PROFILE.sha256,
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
     (stage === "image-map" && ["captured-image-route-mismatch", "conflicting-image-aliases", "invalid-image-url", "invalid-inline-image-data", "inline-image-byte-mismatch", "missing-captured-image-alias", "unsupported-image-url-scheme"].includes(error.sourceReplayFailurePredicate)) ||
     (stage === "inline-image-evidence" && ["captured-image-route-mismatch", "unmatched-inline-image-evidence"].includes(error.sourceReplayFailurePredicate)))
    ? error.sourceReplayFailurePredicate
    : null;
  const imageDecodeEvidence = category === "image" && stage === "visible-image-decode"
    ? sanitizeSourceReplayImageDecodeEvidence(error?.sourceReplayImageDecodeEvidence)
    : null;
  const imageMapEvidence = category === "image" && stage === "image-map" &&
    failurePredicate === "missing-captured-image-alias"
    ? sanitizeSourceReplayImageMapEvidence(error?.sourceReplayImageMapEvidence)
    : null;
  const extentEvidence = category === "replay" && stage === "native-terminal-profile-bottom"
    ? sanitizeSourceReplayExtentEvidence(error?.sourceReplayExtentEvidence)
    : null;
  const nativeMovementEvidence = category === "replay" && stage === "native-movement"
    ? sanitizeSourceReplayNativeMovementEvidence(error?.sourceReplayNativeMovementEvidence)
    : null;
  const terminalClampObservations = category === "replay"
    ? sanitizeSourceReplayTerminalClampObservations(error?.sourceReplayTerminalClampObservations)
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
    extentEvidence,
    nativeMovementEvidence,
    terminalClampObservations,
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
    ...(diagnostic.extentEvidence ? { extentEvidence: diagnostic.extentEvidence } : {}),
    ...(diagnostic.nativeMovementEvidence ? { nativeMovementEvidence: diagnostic.nativeMovementEvidence } : {}),
    ...(diagnostic.terminalClampObservations ? { terminalClampObservations: diagnostic.terminalClampObservations } : {}),
  };
}

export function sanitizeSourceReplayNativeMovementEvidence(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const maximumViewportIndex = MAX_REPLAY_STEPS - 1;
  const maximumMovementIndex = 16;
  const maximumAllowedOffsetViewportUnits = 100_000;
  const maximumPixelExtent = 100_000_000;
  const boundedIndex = (candidate, maximum) => Number.isSafeInteger(candidate) &&
    candidate >= 0 && candidate <= maximum ? candidate : null;
  const boundedOffset = candidate => Number.isFinite(candidate) &&
    candidate >= 0 && candidate <= maximumAllowedOffsetViewportUnits
    ? Number(candidate.toFixed(3))
    : null;
  const boundedPixels = (candidate, minimum = 0) => Number.isSafeInteger(candidate) &&
    candidate >= minimum && candidate <= maximumPixelExtent ? candidate : null;

  const viewportIndex = boundedIndex(value.viewportIndex, maximumViewportIndex);
  const movementIndex = boundedIndex(value.movementIndex, maximumMovementIndex);
  const requestedOffsetViewportUnits = boundedOffset(value.requestedOffsetViewportUnits);
  const actualOffsetViewportUnits = boundedOffset(value.actualOffsetViewportUnits);
  const maximumOffsetViewportUnits = boundedOffset(value.maximumOffsetViewportUnits);
  const documentHeightPx = boundedPixels(value.documentHeightPx);
  const viewportHeightPx = boundedPixels(value.viewportHeightPx, 1);
  if (viewportIndex === null || movementIndex === null ||
      requestedOffsetViewportUnits === null || actualOffsetViewportUnits === null ||
      maximumOffsetViewportUnits === null || documentHeightPx === null || viewportHeightPx === null) {
    return null;
  }

  return {
    viewportIndex,
    movementIndex,
    requestedOffsetViewportUnits,
    actualOffsetViewportUnits,
    maximumOffsetViewportUnits,
    documentHeightPx,
    viewportHeightPx,
  };
}

export function sanitizeSourceReplayTerminalClampObservations(value) {
  if (!Array.isArray(value) || value.length > MAX_REPLAY_STEPS * 18) return null;
  const maximumViewportIndex = MAX_REPLAY_STEPS - 1;
  const maximumMovementIndex = 16;
  const maximumAllowedOffsetViewportUnits = 100_000;
  const maximumPixelExtent = 100_000_000;
  const observations = [];
  for (const observation of value) {
    if (!observation || typeof observation !== "object" || Array.isArray(observation)) return null;
    const {
      viewportIndex,
      movementIndex,
      requestedOffsetViewportUnits,
      actualOffsetViewportUnits,
      maximumOffsetViewportUnits,
      documentHeightPx,
      viewportHeightPx,
    } = observation;
    if (!Number.isSafeInteger(viewportIndex) || viewportIndex < 0 || viewportIndex > maximumViewportIndex ||
        !Number.isSafeInteger(movementIndex) || movementIndex < -1 || movementIndex > maximumMovementIndex ||
        !Number.isFinite(requestedOffsetViewportUnits) || requestedOffsetViewportUnits < 0 ||
        requestedOffsetViewportUnits > maximumAllowedOffsetViewportUnits ||
        !Number.isFinite(actualOffsetViewportUnits) || actualOffsetViewportUnits < 0 ||
        actualOffsetViewportUnits > maximumAllowedOffsetViewportUnits ||
        !Number.isFinite(maximumOffsetViewportUnits) || maximumOffsetViewportUnits < 0 ||
        maximumOffsetViewportUnits > maximumAllowedOffsetViewportUnits ||
        !Number.isSafeInteger(documentHeightPx) || documentHeightPx < 0 || documentHeightPx > maximumPixelExtent ||
        !Number.isSafeInteger(viewportHeightPx) || viewportHeightPx < 1 || viewportHeightPx > 8192) {
      return null;
    }
    observations.push({
      viewportIndex,
      movementIndex,
      requestedOffsetViewportUnits: Number(requestedOffsetViewportUnits.toFixed(3)),
      actualOffsetViewportUnits: Number(actualOffsetViewportUnits.toFixed(3)),
      maximumOffsetViewportUnits: Number(maximumOffsetViewportUnits.toFixed(3)),
      documentHeightPx,
      viewportHeightPx,
    });
  }
  return observations;
}

export function createTerminalClampObservation({
  viewportIndex,
  maximumViewportIndex,
  movementIndex,
  requestedOffsetViewportUnits,
  actualOffsetViewportUnits,
  documentHeightPx,
  viewportHeightPx,
  positionTolerance,
}) {
  if (!Number.isSafeInteger(maximumViewportIndex) || maximumViewportIndex < 0 ||
      maximumViewportIndex >= 512 ||
      !Number.isSafeInteger(viewportIndex) || viewportIndex < 0 || viewportIndex > maximumViewportIndex ||
      !Number.isSafeInteger(movementIndex) || movementIndex < -1 || movementIndex > 16 ||
      !Number.isFinite(requestedOffsetViewportUnits) || requestedOffsetViewportUnits < 0 ||
      requestedOffsetViewportUnits > 100_000 ||
      !Number.isFinite(actualOffsetViewportUnits) || actualOffsetViewportUnits < 0 ||
      actualOffsetViewportUnits > 100_000 ||
      !Number.isSafeInteger(documentHeightPx) || documentHeightPx < 1 || documentHeightPx > 100_000_000 ||
      !Number.isSafeInteger(viewportHeightPx) || viewportHeightPx < 64 || viewportHeightPx > 8192 ||
      !Number.isFinite(positionTolerance) || positionTolerance <= 0 ||
      positionTolerance > 1 / viewportHeightPx + 0.000001) {
    return null;
  }

  const maximumOffsetViewportUnits = Math.max(0, documentHeightPx - viewportHeightPx) / viewportHeightPx;
  if (requestedOffsetViewportUnits <= maximumOffsetViewportUnits + positionTolerance ||
      Math.abs(actualOffsetViewportUnits - maximumOffsetViewportUnits) > positionTolerance) {
    return null;
  }
  return {
    viewportIndex,
    movementIndex,
    requestedOffsetViewportUnits,
    actualOffsetViewportUnits,
    maximumOffsetViewportUnits,
    documentHeightPx,
    viewportHeightPx,
  };
}

export function isTerminalBottomCaptureRepeat({
  terminalClampStarted,
  captureWasClamped,
  captureOffsetViewportUnits,
  previousCaptureOffsetViewportUnits,
  positionTolerance,
}) {
  if (terminalClampStarted !== true || captureWasClamped !== true ||
      !Number.isFinite(captureOffsetViewportUnits) || captureOffsetViewportUnits < 0 ||
      !Number.isFinite(previousCaptureOffsetViewportUnits) || previousCaptureOffsetViewportUnits < 0 ||
      !Number.isFinite(positionTolerance) || positionTolerance <= 0 || positionTolerance > 1) {
    return false;
  }
  const delta = captureOffsetViewportUnits - previousCaptureOffsetViewportUnits;
  return delta >= -positionTolerance / 4 && Math.abs(delta) <= positionTolerance / 4;
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

// The captured GitHub article is an inert, detached DOM. Its serialized
// computed styles supply visibility evidence; the live source article also
// supplies getComputedStyle. Treat uncertain geometry as visible so a missing
// semantic element cannot silently pass the completeness gate.
export function hasVisibleUnsupportedSemanticMarkup(root, allowGitHubChrome = false) {
  if (!root?.querySelectorAll) return true;
  for (const node of root.querySelectorAll("audio,canvas,embed,form,iframe,math,object,svg,video")) {
    // GitHub injects hidden-to-AT Octicons for heading permalinks, code copy
    // controls, and alert titles into the captured article. They are UI chrome,
    // not authored README SVG content. The source replay never enables this
    // exception, so authored SVG (including a forged Octicon) remains evidence.
    if (allowGitHubChrome && node.localName === "svg" &&
        node.getAttribute("data-component") === "Octicon" &&
        node.getAttribute("aria-hidden") === "true" &&
        node.classList.contains("octicon")) {
      const githubPermalinkIcon = node.classList.contains("octicon-link") &&
        node.closest('a.anchor[aria-label^="Permalink:"]');
      const githubCopyIcon =
        (node.classList.contains("js-clipboard-copy-icon") ||
          node.classList.contains("js-clipboard-check-icon")) &&
        node.closest("clipboard-copy.js-clipboard-copy");
      const alertTitle = node.parentElement?.matches("p.markdown-alert-title")
        ? node.parentElement
        : null;
      const githubWarningAlertIcon = node.classList.contains("octicon-alert") &&
        alertTitle?.parentElement?.matches("div.markdown-alert-warning");
      if (githubPermalinkIcon || githubCopyIcon || githubWarningAlertIcon) continue;
    }
    let hidden = false;
    for (let current = node; current; current = current.parentElement) {
      if (current.hasAttribute("hidden")) {
        hidden = true;
        break;
      }
      const inlineStyle = current.style;
      const computedStyle = current.ownerDocument.defaultView?.getComputedStyle(current);
      for (const style of [inlineStyle, computedStyle]) {
        if (!style) continue;
        if (style.display === "none" || style.visibility === "hidden" ||
            style.visibility === "collapse" || style.contentVisibility === "hidden" ||
            (style.opacity !== "" && Number(style.opacity) === 0)) {
          hidden = true;
          break;
        }
      }
      if (hidden) break;
      if (current.tagName === "DETAILS" && !current.hasAttribute("open")) {
        const summary = current.querySelector(":scope > summary");
        if (!summary?.contains(node)) {
          hidden = true;
          break;
        }
      }
      if (current === root) break;
    }
    if (!hidden) return true;
  }
  return false;
}

export function sourceReplayFunctionDeclaration() {
  return String.raw`async function(baseUrl, expected, imageWaitMs, maximumSteps, viewportStepRatio, nativeViewportProfile) {
    const shouldCountSourceReplayLink = ${shouldCountSourceReplayLink.toString()};
    const blockedExternalUris = new Set();
    const inlineObjectUrls = [];
    const imageDecodeEvidenceByElement = new WeakMap();
    let retainInlineObjectUrls = false;
    let imageDecodeEvidence = null;
    let terminalClampObservations = [];
    let terminalClampStarted = false;
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
      if (["captured-image-route-mismatch", "conflicting-image-aliases", "invalid-image-dimension", "invalid-image-url", "invalid-inline-image-data",
           "inline-image-byte-mismatch", "missing-captured-image-alias", "responsive-image-candidates",
           "sanitized-image-count", "unmatched-inline-image-evidence", "unsupported-image-url-scheme"]
          .includes(failurePredicate)) error.sourceReplayFailurePredicate = failurePredicate;
      if (failurePredicate === "missing-captured-image-alias" && failureEvidence) {
        error.sourceReplayImageMapEvidence = failureEvidence;
      }
      throw error;
    };
    const failNativeMovement = (message, viewportIndex, movementIndex, requestedOffset, actualOffset) => {
      const documentHeightPx = document.documentElement.scrollHeight;
      const error = new Error(message);
      error.sourceReplayNativeMovementEvidence = {
        viewportIndex,
        movementIndex,
        requestedOffsetViewportUnits: requestedOffset,
        actualOffsetViewportUnits: actualOffset,
        maximumOffsetViewportUnits: Math.max(0, documentHeightPx - innerHeight) / innerHeight,
        documentHeightPx,
        viewportHeightPx: innerHeight,
      };
      throw error;
    };
    const createUniqueCapturedImageAltRouteMap = (${createUniqueCapturedImageAltRouteMap.toString()});
    const createUniqueCapturedDecorativeImageExclusionMap = (${createUniqueCapturedDecorativeImageExclusionMap.toString()});
    const validateNativeCaptureObservation = (${validateNativeCaptureObservation.toString()});
    const nextSourceTailOffset = (${nextSourceTailOffset.toString()});
    const createTerminalClampObservation = (${createTerminalClampObservation.toString()});
    const isTerminalBottomCaptureRepeat = (${isTerminalBottomCaptureRepeat.toString()});
    const createSemanticHmacDigester = (${sourceReplayHmacDigestFunctionDeclaration()});
    const createSemanticTokenDigestCounts = (${sourceReplayTokenDigestFunctionDeclaration()});
    const hasVisibleUnsupportedSemanticMarkup = (${hasVisibleUnsupportedSemanticMarkup.toString()});
    try {
      const digestSemanticText = await createSemanticHmacDigester(expected.semanticDigestKey);
      if (innerWidth !== expected.viewport.width || innerHeight !== expected.viewport.height ||
          Math.abs(devicePixelRatio - expected.viewport.deviceScaleFactor) > 0.01) {
        fail("Edge content viewport does not match the measured native Markdown host viewport.");
      }
      document.documentElement.dataset.colorScheme = expected.colorScheme;
      document.documentElement.style.colorScheme = expected.colorScheme;
      failureStage = "style-profile";
      const profileLink = document.querySelector("link[data-markdown-style-profile]");
      const profileStylesheetUrl = profileLink ? new URL(profileLink.href, location.href) : null;
      const markdownRoot = document.querySelector("#readme");
      const profileStyle = markdownRoot ? getComputedStyle(markdownRoot) : null;
      if (!profileLink || !profileLink.sheet || !profileStylesheetUrl ||
          profileStylesheetUrl.origin !== location.origin ||
          profileStylesheetUrl.pathname !== expected.styleProfile.cssPath ||
          profileLink.dataset.markdownStyleProfile !== expected.styleProfile.id ||
          profileLink.dataset.markdownStyleProfileVersion !== String(expected.styleProfile.version) ||
          profileLink.integrity !== expected.styleProfile.integrity ||
          expected.styleProfile.id !== "${PINNED_GITHUB_README_STYLE_PROFILE.id}" ||
          expected.styleProfile.version !== ${PINNED_GITHUB_README_STYLE_PROFILE.version} ||
          expected.styleProfile.colorScheme !== "light" ||
          expected.styleProfile.sha256 !== "${PINNED_GITHUB_README_STYLE_PROFILE.sha256}" ||
          profileStyle?.color !== "rgb(31, 35, 40)" || profileStyle?.fontSize !== "16px") {
        fail("Edge did not load the exact pinned GitHub Primer Light Markdown style profile.");
      }
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
          manifest.browserRender.status !== "passed" ||
          manifest.browserRender.sha256 !== expected.renderedHtmlSha256 ||
          manifest.browserRender.byteSize !== expected.renderedHtmlByteSize ||
          !Number.isSafeInteger(manifest.browserRender.byteSize) ||
          manifest.browserRender.byteSize <= 0 ||
          manifest.browserRender.byteSize > 32 * 1024 * 1024) {
        fail("Edge source replay manifest identity does not match the captured README.");
      }
      if (!Array.isArray(manifest.imageRoutes) ||
          !Number.isSafeInteger(expected.capturedImageRouteCount) ||
          expected.capturedImageRouteCount < 0 || expected.capturedImageRouteCount > 15000 ||
          manifest.imageRoutes.length !== expected.capturedImageRouteCount) {
        fail("Captured image-route identities do not match the verified same-byte manifest.",
          "captured-image-route-mismatch");
      }
      const createCapturedImageRouteIdentityMatcher = (${createCapturedImageRouteIdentityMatcher.toString()});
      const capturedImageRouteMatcher = createCapturedImageRouteIdentityMatcher(manifest.imageRoutes);
      if (!capturedImageRouteMatcher) {
        fail("Captured image routes contain malformed or ambiguous identities.",
          "captured-image-route-mismatch");
      }
      failureStage = "rendered-html-response";
      const renderedResponse = await fetch(baseUrl + "/rendered-html", {
        cache: "no-store", credentials: "omit", redirect: "error",
      });
      if (!renderedResponse.ok ||
          renderedResponse.headers.get("content-type") !== "text/html; charset=utf-8" ||
          Number(renderedResponse.headers.get("content-length")) !== expected.renderedHtmlByteSize ||
          renderedResponse.headers.get("x-content-sha256") !== expected.renderedHtmlSha256) {
        fail("Edge did not receive the hash-matched captured GitHub article.");
      }
      const renderedBytes = await renderedResponse.arrayBuffer();
      failureStage = "rendered-html-digest";
      if (renderedBytes.byteLength !== expected.renderedHtmlByteSize ||
          await hashBytes(renderedBytes) !== expected.renderedHtmlSha256) {
        fail("Edge captured GitHub article bytes failed their SHA-256 check.");
      }
      const capturedDocument = new DOMParser().parseFromString(
        new TextDecoder("utf-8", { fatal: true }).decode(renderedBytes), "text/html");
      if (capturedDocument.body.firstElementChild?.tagName !== "ARTICLE") {
        fail("Edge captured GitHub article has no valid root.");
      }
      const capturedVisibleUnsupportedMarkup =
        hasVisibleUnsupportedSemanticMarkup(capturedDocument.body, true);
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
      failureStage = "decode-readme";
      // .NET's Encoding.UTF8.GetString preserves an initial BOM as U+FEFF.
      // Match that exact decoded string, then bind it to JitHub's independently
      // recorded digest before starting any client-render timing.
      const markdown = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(readmeBytes);
      const decodedTextSha256 = await hashText(markdown);
      failureStage = "native-source-identity";
      if (nativeViewportProfile &&
          nativeViewportProfile.decodedTextSha256.toLowerCase() !== decodedTextSha256.toLowerCase()) {
        fail("Native viewport profile is not bound to the exact decoded README source.");
      }
      failureStage = "parser-pin";
      if (expected.parser.name !== "marked" || expected.parser.license !== "MIT" ||
          expected.parser.sha256 !== "2dc4769dfde29f51c7aca1a539c6407c789c8ea644cf8b7d01ded28a9c1d800b" ||
          !window.marked || typeof window.marked.parse !== "function" ||
          (window.marked.version && window.marked.version !== expected.parser.version)) {
        fail("Edge did not load the pinned Marked parser bundle.");
      }
      const expectedEmojiPins = expected.parser;
      if (expectedEmojiPins.emojiShortcodeSource !== "github/gemoji/db/emoji.json" ||
          expectedEmojiPins.emojiShortcodeSourceCommit !== "fadaeaf1f1a9be82b321316a6c5502e43138b2f6" ||
          expectedEmojiPins.emojiShortcodeLicense !== "MIT" ||
          expectedEmojiPins.emojiShortcodeSourceSha256 !== "b174ae2aeb321b52f64adb9ff412f966a7f338839d780784dd15dcad702c2dd6" ||
          expectedEmojiPins.emojiShortcodeLicenseSha256 !== "bab932cd28676c262d38eb7aedd7c366ebae15a07d823cc51854ec16ec045dba" ||
          expectedEmojiPins.emojiShortcodeMapSha256 !== "44980486df12fa536479d4a8c28e57b65654b373a263a894f79ccc7ceb335cf5" ||
          expectedEmojiPins.emojiShortcodeExtensionSha256 !== "89f772b065e6ba0114a9a8aaba19de071b20e2c44af065f83d09865c5bcb4d87" ||
          !Number.isSafeInteger(expectedEmojiPins.emojiShortcodeAliasCount) ||
          expectedEmojiPins.emojiShortcodeAliasCount !== 1913) {
        fail("Edge GitHub emoji shortcode provenance does not match the fixed offline pin.");
      }
      failureStage = "emoji-shortcode-map";
      const emojiMapResponse = await fetch(baseUrl + "/github-emoji-shortcodes.json", {
        cache: "no-store", credentials: "omit", redirect: "error",
      });
      if (!emojiMapResponse.ok ||
          emojiMapResponse.headers.get("x-content-sha256") !== expectedEmojiPins.emojiShortcodeMapSha256) {
        fail("Edge did not receive the hash-matched GitHub emoji shortcode map.");
      }
      const emojiMapBytes = await emojiMapResponse.arrayBuffer();
      if (await hashBytes(emojiMapBytes) !== expectedEmojiPins.emojiShortcodeMapSha256) {
        fail("Edge GitHub emoji shortcode map failed its SHA-256 check.");
      }
      let emojiAliases;
      try {
        emojiAliases = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(emojiMapBytes));
      } catch {
        fail("Edge GitHub emoji shortcode map is not valid UTF-8 JSON.");
      }
      if (!emojiAliases || typeof emojiAliases !== "object" || Array.isArray(emojiAliases) ||
          Object.keys(emojiAliases).length !== expectedEmojiPins.emojiShortcodeAliasCount) {
        fail("Edge GitHub emoji shortcode map has an invalid shape or alias count.");
      }
      for (const [alias, value] of Object.entries(emojiAliases)) {
        if (!/^[a-z0-9_+-]{1,64}$/u.test(alias) || typeof value !== "string" ||
            !/^[^\u0000-\u001f\u007f-\u009f<>]{1,64}$/u.test(value) ||
            Array.from(value).length > 32 ||
            /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(value)) {
          fail("Edge GitHub emoji shortcode map contains an invalid alias or Unicode value.");
        }
      }
      if (typeof window.installPinnedGitHubEmojiShortcodes !== "function" ||
          window.installPinnedGitHubEmojiShortcodes(
            window.marked, emojiAliases, expectedEmojiPins.emojiShortcodeAliasCount) !== true) {
        fail("Edge did not install the pinned GitHub emoji shortcode behavior.");
      }
      const started = performance.now();
      let auditOnlyFrameWaitMs = 0;
      let auditOnlyFrameWaitCount = 0;
      failureStage = "parse-markdown";
      const parsedHtml = window.marked.parse(markdown, { gfm: true, breaks: false, pedantic: false, async: false });
      if (typeof parsedHtml !== "string") fail("Pinned Marked parser returned a non-string result.");
      const parsedDocument = new DOMParser().parseFromString(parsedHtml, "text/html");
      const parsedElements = [...parsedDocument.body.querySelectorAll("*")];
      if (parsedElements.length > 200000) fail("Pinned Markdown parse exceeded the bounded HTML element count.");
      const rawUnsupportedSemanticMarkupCount = parsedDocument.body.querySelectorAll(
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
      const supportedPictureSourceTypes = new Set(["image/gif", "image/jpeg", "image/png"]);
      const singlePictureSourceUrl = source => {
        const sourceSet = (source.getAttribute("srcset") || "").trim();
        if (!sourceSet || sourceSet.length > 8192 || sourceSet.includes(",")) return null;
        const candidates = sourceSet.split(/\s+/u);
        return candidates.length === 1 ? candidates[0] : null;
      };
      // Native SafeHtml rendering supports only a deterministic subset of
      // <picture>: one explicitly supported raster type and one URL candidate,
      // with no media/sizes or DPR negotiation. Normalize that exact subset to
      // the fallback img before route hashing; all other sources remain ignored
      // and the authored img fallback stays subject to the normal hard gates.
      for (const picture of parsedDocument.body.querySelectorAll("picture")) {
        const image = picture.querySelector(":scope > img");
        if (!image) continue;
        let selectedSource = null;
        let selectedUrl = null;
        const sources = [...picture.querySelectorAll(":scope > source")];
        const expectedColorScheme = expected.colorScheme.toLowerCase();
        for (const source of sources) {
          const media = source.getAttribute("media");
          const normalizedMedia = (media || "").replace(/\s+/gu, "").toLowerCase();
          if (!media || !normalizedMedia.includes("prefers-color-scheme:" + expectedColorScheme)) continue;
          const candidate = singlePictureSourceUrl(source);
          if (!candidate) continue;
          selectedSource = source;
          selectedUrl = candidate;
          break;
        }
        for (const source of sources) {
          if (selectedSource) break;
          const type = (source.getAttribute("type") || "").trim().toLowerCase();
          if (source.hasAttribute("media") || source.hasAttribute("sizes") ||
              !supportedPictureSourceTypes.has(type)) continue;
          const candidate = singlePictureSourceUrl(source);
          if (!candidate) continue;
          selectedSource = source;
          selectedUrl = candidate;
          break;
        }
        if (!selectedSource || !selectedUrl) continue;
        image.setAttribute("src", selectedUrl);
        for (const dimension of ["width", "height"]) {
          if (selectedSource.hasAttribute(dimension)) {
            image.setAttribute(dimension, selectedSource.getAttribute(dimension) || "");
          }
        }
      }
      const parsedImages = [...parsedDocument.body.querySelectorAll("img")];
      const authoredImageDimensions = new WeakMap();
      for (const image of parsedImages) {
        const dimensions = { width: null, height: null };
        for (const dimension of ["width", "height"]) {
          if (!image.hasAttribute(dimension)) continue;
          const value = (image.getAttribute(dimension) || "").trim();
          const pixelMatch = /^([1-9][0-9]{0,4})(?:px)?$/iu.exec(value);
          const percentageMatch = /^(?:[1-9][0-9]?|100)%$/u.exec(value);
          // Some pinned READMEs use an HTML width hint such as "60px;".
          // Preserve it verbatim so Edge applies its own HTML behavior, but
          // still bound the numeric part before admitting the attribute.
          const legacyPixelMatch = /^([1-9][0-9]{0,4})px;$/iu.exec(value);
          const pixels = pixelMatch || legacyPixelMatch
            ? Number((pixelMatch || legacyPixelMatch)[1]) : Number.NaN;
          if (!percentageMatch && (!Number.isSafeInteger(pixels) || pixels > 16384)) {
            fail("Markdown image has an invalid authored width or height.", "invalid-image-dimension");
          }
          // Percentages are bounded by the replay container and remain in
          // the sanitized HTML. Keep malformed-but-common semicolon hints
          // verbatim too; only canonical absolute pixel hints can establish
          // an explicit ratio without changing how Edge interprets them.
          dimensions[dimension] = percentageMatch || legacyPixelMatch ? null : pixels;
          image.setAttribute(dimension, value);
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
      const allImageSources = sourceBearingImages.map(image => image.getAttribute("src"));
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
      failureStage = "viewport";
      document.body.replaceChildren(article);
      document.body.style.margin = "0";
      const viewportWidthEvidence = {
        documentClientWidth: document.documentElement.clientWidth,
        bodyClientWidth: document.body.clientWidth,
        articleWidth: article.getBoundingClientRect().width,
      };
      if (viewportWidthEvidence.documentClientWidth !== innerWidth ||
          viewportWidthEvidence.bodyClientWidth !== innerWidth ||
          !Number.isFinite(viewportWidthEvidence.articleWidth) ||
          Math.abs(viewportWidthEvidence.articleWidth - innerWidth) > 0.5) {
        fail("Source-replay content width does not match the native content viewport.");
      }
      const sourceBearingImageSet = new Set(sourceBearingImages);
      const allImages = [...article.querySelectorAll("img")]
        .filter(image => sourceBearingImageSet.has(image) && isMarkdownVisibleImage(image));
      if (allImages.length !== allImageSources.length) {
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
      const exclusionProofs = manifest.nonRenderedDecorativeImageExclusions || [];
      if (!Array.isArray(exclusionProofs) || exclusionProofs.length > 128) {
        fail("Captured non-rendered decorative-image evidence is malformed.");
      }
      let excludedSourceImageIndexes = new Set();
      if (exclusionProofs.length > 0) {
        const sourceImageCandidates = await Promise.all(allImages.map(async (image, index) => {
          const explicitEmptyAlt = image.hasAttribute("alt") && image.getAttribute("alt") === "";
          const source = allImageSources[index];
          const candidateUrlSha256s = /^data:image\//iu.test(source)
            ? []
            : await Promise.all(normalizedUrlCandidates(source).map(hashText));
          return { explicitEmptyAlt, candidateUrlSha256s };
        }));
        excludedSourceImageIndexes = createUniqueCapturedDecorativeImageExclusionMap(
          sourceImageCandidates,
          exclusionProofs,
        );
        if (!excludedSourceImageIndexes || excludedSourceImageIndexes.size !== exclusionProofs.length) {
          fail("Captured non-rendered decorative-image evidence did not uniquely match an explicit-empty-alt Markdown source image.");
        }
        // The index only selects the already URL-hash-identified node in this
        // parsed DOM. It is never used to map a source image to a captured route.
        for (const index of excludedSourceImageIndexes) allImages[index].remove();
      }
      const images = allImages.filter((image, index) => !excludedSourceImageIndexes.has(index));
      const imageSources = allImageSources.filter((_, index) => !excludedSourceImageIndexes.has(index));
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
        const route = capturedRouteByIndex.get(item?.index);
        if (!Number.isSafeInteger(item?.index) || !route || route.dataUri !== true ||
            typeof item.mimeType !== "string" || !/^[0-9a-f]{64}$/iu.test(item.sha256 || "")) {
          failureStage = "inline-image-evidence";
          fail("Captured inline-image identity is malformed or unbound to its route.",
            "unmatched-inline-image-evidence");
        }
        const key = item.mimeType + ":" + item.sha256;
        const routeIndexes = remainingInlineImages.get(key) || [];
        routeIndexes.push(item.index);
        remainingInlineImages.set(key, routeIndexes);
      }
      let inlineImageBytes = 0;
      failureStage = "image-map";
      if (images.length !== capturedImageRouteMatcher.routeCount) {
        fail("Visible Markdown image count does not match the captured GitHub image routes.",
          "captured-image-route-mismatch");
      }
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
          const routeIndexes = remainingInlineImages.get(key) || [];
          const capturedRouteIndex = routeIndexes.shift();
          if (!Number.isSafeInteger(capturedRouteIndex) ||
              capturedImageRouteMatcher.claim({
                routeIndex: capturedRouteIndex,
                sha256: digest,
                mimeType: decoded.mimeType,
                dataUri: true,
              }) === null) {
            fail("Markdown inline image bytes do not match a captured visible GitHub image.",
              "captured-image-route-mismatch");
          }
          remainingInlineImages.set(key, routeIndexes);
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
        const sourceAltSha256 = sourceImageAltHashes[index]?.toLowerCase();
        const capturedRouteIndex = sourceAltSha256 && semanticAltRouteMap?.get(sourceAltSha256);
        let entry = matchingEntries[0];
        let imageAssetSource = "";
        if (!entry) {
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
            const candidateRouteIndexes = new Set();
            for (const route of manifest.imageRoutes) {
              if (route.dataUri !== true && candidateHashes.includes(route.urlSha256)) {
                candidateRouteIndexes.add(route.index);
              }
            }
            if (Number.isSafeInteger(capturedRouteIndex)) candidateRouteIndexes.add(capturedRouteIndex);
            const routeCandidates = [...candidateRouteIndexes]
              .map(routeIndex => capturedRouteByIndex.get(routeIndex))
              .filter(route => route && route.dataUri !== true &&
                /^[0-9a-f]{64}$/iu.test(route.urlSha256 || ""))
              .slice(0, 8)
              .map(route => {
                const capturedPayload = assetByUrlHash.get(route.urlSha256);
                const candidate = { routeIndex: route.index, urlSha256: route.urlSha256 };
                if (capturedPayload && capturedPayload.sha256 === route.sha256 &&
                    capturedPayload.mimeType === route.mimeType) {
                  candidate.payloadSha256 = capturedPayload.sha256;
                  candidate.mimeType = capturedPayload.mimeType;
                }
                return candidate;
              });
            fail("Markdown image asset is missing from the captured URL map.", "missing-captured-image-alias", {
              imageIndex: index,
              candidateUrlSha256s: candidateHashes,
              capturedAssetUrlEntryCount: assetByUrlHash.size,
              sourceRouteIndex: Number.isSafeInteger(capturedRouteIndex) ? capturedRouteIndex : null,
              routeCandidates,
            });
          }
        }
        const matchedRouteIndex = capturedImageRouteMatcher.claim({
          candidateUrlSha256s: candidateHashes,
          routeIndex: Number.isSafeInteger(capturedRouteIndex) ? capturedRouteIndex : null,
          sha256: entry.sha256,
          mimeType: entry.mimeType,
        });
        if (!Number.isSafeInteger(matchedRouteIndex)) {
          fail("Markdown image identity could not be reconciled one-to-one with a captured GitHub image route.",
            "captured-image-route-mismatch");
        }
        expectedUrlHashes.add(entry.urlSha256);
        imageDecodeEvidenceByElement.set(images[index], {
          imageIndex: index,
          capturedUrlSha256: entry.urlSha256,
          capturedImageRouteIndex: matchedRouteIndex,
          dataUri: false,
        });
        pendingImageSources.set(images[index], imageAssetSource ||
          baseUrl + "/asset-by-url-sha256/" + entry.urlSha256);
      }
      if ([...remainingInlineImages.values()].some(routeIndexes => routeIndexes.length !== 0) ||
          !capturedImageRouteMatcher.isComplete()) {
        failureStage = "inline-image-evidence";
        fail("A captured GitHub inline image is missing from the pinned Markdown source.",
          "captured-image-route-mismatch");
      }
      window.__jithubSourceReplayObjectUrls = inlineObjectUrls;
      retainInlineObjectUrls = true;
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
          for (let movementIndex = 0;
            movementIndex < viewport.movementOffsetsViewportUnits.length;
            movementIndex++) {
            const movementOffset = viewport.movementOffsetsViewportUnits[movementIndex];
            if (movementCount >= maximumSteps * 17) {
              fail("Source-bound Edge traversal exceeded its bounded native movement count.");
            }
            const expectedTop = movementOffset;
            if (terminalClampStarted) {
              const currentHeight = document.documentElement.scrollHeight;
              const currentMaximumTop = Math.max(0, currentHeight - innerHeight) / innerHeight;
              if (expectedTop <= currentMaximumTop + positionTolerance) {
                failNativeMovement(
                  "Source-bound Edge native profile attempted to leave its terminal bottom after a clamp.",
                  viewportIndex,
                  movementIndex,
                  expectedTop,
                  scrollY / innerHeight);
              }
            }
            scrollTo(0, expectedTop * innerHeight);
            let stable = false;
            let movementWasTerminalClamped = false;
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
                const currentMaximumTop = Math.max(0, currentHeight - innerHeight) / innerHeight;
                if (expectedTop <= currentMaximumTop + positionTolerance && pass < 7) {
                  // Decoding an image can resize content above the viewport.
                  // Scroll anchoring then moves us after the original scrollTo.
                  // The target is still reachable, so reissue it and require a
                  // fresh, image-ready stable frame pair. Never reinterpret
                  // this as a terminal clamp or widen the position tolerance.
                  scrollTo(0, expectedTop * innerHeight);
                  previousHeight = -1;
                  previousTop = Number.NaN;
                  continue;
                }
                if (previousHeight >= 0 && currentHeight === previousHeight &&
                    Math.abs(actualTop - previousTop) <= positionTolerance / 4) {
                  const terminalClampObservation = createTerminalClampObservation({
                    viewportIndex,
                    maximumViewportIndex: maximumSteps - 1,
                    movementIndex,
                    requestedOffsetViewportUnits: expectedTop,
                    actualOffsetViewportUnits: actualTop,
                    documentHeightPx: currentHeight,
                    viewportHeightPx: innerHeight,
                    positionTolerance,
                  });
                  if (terminalClampObservation &&
                      (!terminalClampStarted || expectedTop > terminalClampObservation.maximumOffsetViewportUnits +
                        positionTolerance)) {
                    if (terminalClampObservations.length >= maximumSteps * 18) {
                      fail("Source-bound Edge terminal-clamp evidence exceeded its bounded observation count.");
                    }
                    terminalClampStarted = true;
                    movementWasTerminalClamped = true;
                    terminalClampObservations.push(terminalClampObservation);
                    // The stable frame pair proves a bottom clamp, not target convergence.
                    auditOnlyFrameWaitMs += passFrameWaitMs;
                    auditOnlyFrameWaitCount += 3;
                    stable = true;
                    break;
                  }
                  failNativeMovement(
                    "Source-bound Edge could not reproduce a settled native movement offset.",
                    viewportIndex,
                    movementIndex,
                    expectedTop,
                    actualTop);
                }
                previousHeight = currentHeight;
                previousTop = actualTop;
                continue;
              }
              if (previousHeight >= 0 && currentHeight === previousHeight &&
                  Math.abs(actualTop - previousTop) <= positionTolerance / 4) {
                if (terminalClampStarted) {
                  const maximumTop = Math.max(0, currentHeight - innerHeight) / innerHeight;
                  if (expectedTop <= maximumTop + positionTolerance ||
                      Math.abs(actualTop - maximumTop) > positionTolerance) {
                    failNativeMovement(
                      "Source-bound Edge native profile re-entered content after its terminal bottom clamp.",
                      viewportIndex,
                      movementIndex,
                      expectedTop,
                      actualTop);
                  }
                }
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
            if (!stable ||
                (Math.abs(actualTop - expectedTop) > positionTolerance && !movementWasTerminalClamped)) {
              failNativeMovement(
                "Source-bound Edge movement did not settle at the recorded native offset.",
                viewportIndex,
                movementIndex,
                expectedTop,
                actualTop);
            }
            movementCount++;
          }

          failureStage = "native-capture-position";
          const captureTop = scrollY / innerHeight;
          const captureDocumentHeightPx = document.documentElement.scrollHeight;
          const captureMaximumTop = Math.max(0, captureDocumentHeightPx - innerHeight) / innerHeight;
          const captureMatchesNative = Math.abs(captureTop - viewport.captureOffsetViewportUnits) <= positionTolerance;
          if (terminalClampStarted &&
              (viewport.captureOffsetViewportUnits <= captureMaximumTop + positionTolerance ||
               Math.abs(captureTop - captureMaximumTop) > positionTolerance)) {
            fail("Source-bound Edge native capture left its stable terminal bottom after a clamp.");
          }
          let captureClampObservation = null;
          if (!captureMatchesNative) {
            captureClampObservation = createTerminalClampObservation({
              viewportIndex,
              maximumViewportIndex: maximumSteps - 1,
              movementIndex: -1,
              requestedOffsetViewportUnits: viewport.captureOffsetViewportUnits,
              actualOffsetViewportUnits: captureTop,
              documentHeightPx: captureDocumentHeightPx,
              viewportHeightPx: innerHeight,
              positionTolerance,
            });
            if (!terminalClampStarted || !captureClampObservation ||
                terminalClampObservations.length >= maximumSteps * 18) {
              fail("Source-bound Edge capture did not match its native offset or a verified terminal bottom.");
            }
            terminalClampObservations.push(captureClampObservation);
          }
          const captureValidation = validateNativeCaptureObservation(
            captureTop,
            captureClampObservation ? captureTop : viewport.captureOffsetViewportUnits,
            previousCaptureOffset,
            positionTolerance,
            viewportStepRatio,
          );
          if (!captureValidation.positionMatches) {
            fail("Source-bound Edge capture offsets did not match the native viewport profile.");
          }
          failureStage = "native-capture-step";
          const terminalBottomCaptureRepeat = isTerminalBottomCaptureRepeat({
            terminalClampStarted,
            captureWasClamped: captureClampObservation !== null,
            captureOffsetViewportUnits: captureTop,
            previousCaptureOffsetViewportUnits: previousCaptureOffset,
            positionTolerance,
          });
          if (!captureValidation.stepPreservesOverlap && !terminalBottomCaptureRepeat) {
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
      if (terminalClampObservations.length > 0) {
        const finalMaximumTop = Math.max(0, height - innerHeight) / innerHeight;
        for (const observation of terminalClampObservations) {
          if (observation.documentHeightPx !== height ||
              observation.viewportHeightPx !== innerHeight ||
              Math.abs(observation.maximumOffsetViewportUnits - finalMaximumTop) > positionTolerance ||
              Math.abs(observation.actualOffsetViewportUnits - finalMaximumTop) > positionTolerance) {
            fail("Source-bound Edge terminal-clamp evidence did not remain pinned to the final document bottom.");
          }
        }
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
        capturedVisibleUnsupportedMarkup,
        rawUnsupportedSemanticMarkupCount,
        createSemanticTokenDigestCounts,
        hashText,
        hashBytes,
      });
      await new Promise(resolve => setTimeout(resolve, 50));
      return {
        ok: true,
        decodedTextSha256,
        viewport: {
          width: innerWidth,
          height: innerHeight,
          deviceScaleFactor: devicePixelRatio,
          colorScheme: expected.colorScheme,
          documentClientWidth: viewportWidthEvidence.documentClientWidth,
          bodyClientWidth: viewportWidthEvidence.bodyClientWidth,
          articleWidth: viewportWidthEvidence.articleWidth,
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
          terminalClampObservations,
          viewportProfileSha256: nativeViewportProfile?.sha256 || "",
          captureOffsetsViewportUnits,
        },
        width: Math.min(width, innerWidth),
        height,
        expectedImageCount: images.length,
        verifiedImageCount: capturedImageRouteMatcher.claimedCount,
        firstViewportRealizedImageCount,
        expectedUrlHashes: [...expectedUrlHashes],
        semanticImageAliasMatchCount,
        styleProfileEvidence: {
          id: expected.styleProfile.id,
          version: expected.styleProfile.version,
          sha256: expected.styleProfile.sha256,
          loaded: Boolean(profileLink?.sheet),
          integrityAttributeMatched: profileLink?.integrity === expected.styleProfile.integrity,
        },
        blockedExternalUris: [...blockedExternalUris],
        semantic,
      };
    } catch (error) {
      return {
        ok: false,
        failureStage,
        failurePredicate: ["captured-image-route-mismatch", "conflicting-image-aliases", "invalid-image-dimension", "invalid-image-url", "invalid-inline-image-data",
          "inline-image-byte-mismatch", "missing-captured-image-alias", "responsive-image-candidates",
          "sanitized-image-count", "unmatched-inline-image-evidence", "unsupported-image-url-scheme"]
          .includes(error?.sourceReplayFailurePredicate) ? error.sourceReplayFailurePredicate : undefined,
        imageDecodeEvidence,
        imageMapEvidence: error?.sourceReplayImageMapEvidence &&
          error?.sourceReplayFailurePredicate === "missing-captured-image-alias"
          ? error.sourceReplayImageMapEvidence
          : undefined,
        nativeMovementEvidence: failureStage === "native-movement"
          ? error?.sourceReplayNativeMovementEvidence
          : undefined,
        terminalClampObservations: terminalClampObservations.length > 0
          ? terminalClampObservations
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
      capturedVisibleUnsupportedMarkup,
      rawUnsupportedSemanticMarkupCount,
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
        const hasAccessibleImageName = [...link.querySelectorAll("img[alt]")]
          .some(image => isRendered(image) && clean(image.getAttribute("alt")) !== "");
        if (shouldCountSourceReplayLink(
          clean(link.innerText), href, destination, readmeBaseUrl, hasAccessibleImageName)) {
          distinctLinks.add(destination.toLowerCase());
        }
      }
      const visibleImages = images.filter(isRendered);
      const unsupportedMediaCount = [...article.querySelectorAll("audio,video")].filter(isRendered).length;
      const sourceVisibleUnsupportedMarkup = hasVisibleUnsupportedSemanticMarkup(article);
      const rect = article.getBoundingClientRect();
      const visibleTextTokenCount = Object.values(visibleTextTokenDigests)
        .reduce((sum, count) => sum + count, 0);
      const complete = !capturedVisibleUnsupportedMarkup && !sourceVisibleUnsupportedMarkup &&
        invalidLinkCount === 0 && visibleImages.length === images.length;
      const keyBytes = Uint8Array.from(
        semanticDigestKey.match(/.{2}/gu).map(value => Number.parseInt(value, 16)));
      return {
        complete,
        incompleteReason: complete ? "" : "unsupported-or-unrendered-semantic-content",
        completenessEvidence: {
          capturedVisibleUnsupportedMarkup,
          sourceVisibleUnsupportedMarkup,
          invalidLinkCount,
          unrenderedImageCount: images.length - visibleImages.length,
        },
        rawUnsupportedSemanticMarkupCount,
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

export function shouldCountSourceReplayLink(
  hasVisibleText,
  authoredHref,
  destinationHref,
  readmeBaseUrl,
  hasAccessibleImageName = false,
) {
  if (hasVisibleText || hasAccessibleImageName) return true;

  const trimmedHref = typeof authoredHref === "string" ? authoredHref.trim() : "";
  if (!trimmedHref.startsWith("#")) return true;

  try {
    const destination = new URL(destinationHref);
    const readme = new URL(readmeBaseUrl);
    return destination.origin !== readme.origin ||
      destination.pathname !== readme.pathname ||
      destination.search !== readme.search ||
      destination.hash.length <= 1;
  } catch {
    // URL validation happens before this predicate. Retain a destination if a
    // future caller supplies an invalid base rather than silently undercounting.
    return true;
  }
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
    throw new Error("Usage: node same-byte-edge-source.mjs --corpus=<case-corpus> --out=<output-dir> --width=<native-content-width-dip> --height=<native-content-height-dip> --device-scale-factor=<native-dpi-over-96> [--native-viewport-profile=<json-path>] [--color-scheme=light] [--report=<path>] [--max-tiles=512] [--edge=<msedge.exe>]");
  }
  if (colorScheme !== PINNED_GITHUB_README_STYLE_PROFILE.colorScheme) {
    throw new Error("--color-scheme must be light for the pinned GitHub Primer Light same-byte oracle; app-theme review is a separate QA pass.");
  }
  if (viewportStepRatio >= 1) {
    throw new Error("--viewport-step-ratio must be less than one viewport height.");
  }
  await mkdir(outputDirectory, { recursive: true });
  if (existsSync(reportPath) || existsSync(`${reportPath}.pre-cleanup.json`)) {
    throw new Error("Same-byte source report already exists; use a fresh report path to preserve prior evidence.");
  }
  let replayServer;
  let profileDirectory;
  let edge;
  let cdp;
  let pendingReport;
  let replayFailure;
  let cleanupFailure;
  try {
    profileDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-readme-edge-"));
    replayServer = await createSameByteReplayServer(corpusDirectory);
    if (replayServer.manifest?.browserRender?.status !== "passed") {
      throw new Error("Source-bound Edge replay requires a captured GitHub-rendered article.");
    }
    const launched = await launchSourceReplay({ edgePath, profileDirectory });
    edge = launched.edge;
    cdp = launched.cdp;
    const nativeViewportProfile = await loadNativeViewportProfile(
      nativeViewportProfilePath, height, replayServer);
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
    pendingReport = report;
  } catch (error) {
    replayFailure = error;
    pendingReport = createSourceReplayFailureReport(error);
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
    try {
      if (profileDirectory) {
        await cleanupBrowserProfile(profileDirectory, {
          closeServers: async () => { if (replayServer) await replayServer.close(); },
        });
      } else if (replayServer) {
        await replayServer.close();
      }
    } catch (error) {
      cleanupFailure = error;
    }
  }

  if (cleanupFailure) {
    process.exitCode = 1;
    if (pendingReport?.status === "passed") {
      const preCleanupPath = `${reportPath}.pre-cleanup.json`;
      await writeFile(preCleanupPath, `${JSON.stringify({
        schemaVersion: 1,
        status: "rendered-before-cleanup",
        qualifiesAsCompletedReplay: false,
        report: pendingReport,
      }, null, 2)}\n`, { encoding: "utf8", flag: "wx" });
    }
    const report = {
      schemaVersion: 2,
      status: "failed",
      failureCategory: "cleanup",
      failureStage: cleanupFailure.failureStage,
      errorType: "BrowserProfileCleanupError",
      error: cleanupFailure.message,
      cleanup: {
        profileState: cleanupFailure.profileState,
        profilePreserved: cleanupFailure.profilePreserved,
        serverCloseFailed: cleanupFailure.serverCloseFailed,
      },
      ...(replayFailure ? { replayFailure: pendingReport } : {}),
    };
    await writeFile(reportPath, `${JSON.stringify(report, null, 2)}\n`, { encoding: "utf8", flag: "wx" });
    process.stderr.write(`${JSON.stringify({
      ok: false,
      status: report.status,
      failureCategory: report.failureCategory,
      failureStage: report.failureStage,
      errorType: report.errorType,
      error: report.error,
    })}\n`);
  } else {
    const report = pendingReport || createSourceReplayFailureReport(new Error("Source-bound replay produced no report."));
    await writeFile(reportPath, `${JSON.stringify(report, null, 2)}\n`, { encoding: "utf8", flag: "wx" });
    if (report.status === "passed") {
      process.stdout.write(`${JSON.stringify({ ok: true, report: reportPath, status: report.status })}\n`);
    } else {
      process.exitCode = 1;
      process.stderr.write(`${JSON.stringify({
        ok: false,
        status: report.status,
        failureCategory: report.failureCategory,
        failureStage: report.failureStage,
        errorType: report.errorType,
        error: report.error,
      })}\n`);
    }
  }
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) {
  await runCli();
}
