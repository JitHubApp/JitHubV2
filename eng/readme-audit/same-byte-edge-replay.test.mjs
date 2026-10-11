import assert from "node:assert/strict";
import { createHmac, webcrypto } from "node:crypto";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { waitForDevToolsPort } from "./browser-launch.mjs";
import { cleanupBrowserProfile } from "./browser-profile-cleanup.mjs";
import {
  createSameByteReplayServer,
  gitBlobSha1,
  PINNED_GITHUB_README_STYLE_PROFILE,
  PINNED_SOURCE_REPLAY_PARSER,
  resourceUrlSha256,
  sha256,
} from "./same-byte-corpus.mjs";
import {
  MAX_SAME_BYTE_TRAVERSAL_VIEWPORTS,
  SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO,
} from "./same-byte-traversal.mjs";
import { replayFunctionDeclaration, replaySameByteInEdge } from "./same-byte-edge-replay.mjs";
import { loadPinnedGitHubEmojiShortcodes } from "./vendor/github-emoji-shortcodes.mjs";
import {
  createSourceReplayFailureReport,
  createCapturedImageRouteIdentityMatcher,
  hasVisibleUnsupportedSemanticMarkup,
  sanitizeSourceReplayNativeMovementEvidence,
  createUniqueCapturedImageAltRouteMap,
  readCapturedInlineImageEvidence,
  replaySourceBoundMarkdownInEdge,
  sourceReplayHmacDigestFunctionDeclaration,
  sourceReplayFunctionDeclaration,
  shouldCountSourceReplayLink,
  sourceReplayTokenDigestFunctionDeclaration,
  createTerminalClampObservation,
  isTerminalBottomCaptureRepeat,
  sanitizeSourceReplayTerminalClampObservations,
  nextSourceTailOffset,
  isSourceReplayExtentCompatible,
  validateNativeCaptureObservation,
  validateNativeViewportProfile,
  validateTileCoverage,
} from "./same-byte-edge-source.mjs";

test("source semantic HMAC tokens match the native Rune token contract without persisting text", async () => {
  const createDigester = new Function(
    "crypto", "TextEncoder", `return (${sourceReplayHmacDigestFunctionDeclaration()});`)(webcrypto, TextEncoder);
  const digestTokens = new Function(
    `return (${sourceReplayTokenDigestFunctionDeclaration()});`)();
  const keyHex = "ab".repeat(32);
  const hmac = await createDigester(keyHex);
  const source = "Résumé re\u0301sume\u0301 İß Σς ΟΣ 中文中 \u{20000} 42 42 edge\u20dd mark";
  const actual = await digestTokens(source, hmac);
  const expectedTokens = ["résumé", "résumé", "İß", "σς", "οσ", "中", "文", "中", "\u{20000}", "42", "42", "edge", "mark"];
  const expected = {};
  for (const token of expectedTokens) {
    const normalized = [...token.normalize("NFC")]
      .map(rune => rune === "\u0130" ? rune : rune.toLowerCase())
      .join("");
    const digest = createHmac("sha256", Buffer.from(keyHex, "hex"))
      .update(normalized, "utf8")
      .digest("hex");
    expected[digest] = (expected[digest] || 0) + 1;
  }

  assert.deepEqual(actual, expected);
  assert.equal(Object.values(actual).reduce((sum, count) => sum + count, 0), expectedTokens.length);
  assert.equal(JSON.stringify(actual).includes("résumé"), false);
  assert.equal(JSON.stringify(actual).includes(keyHex), false);
  await assert.rejects(createDigester("not-a-per-run-key"), /digest key is invalid/u);
  assert.match(sourceReplayTokenDigestFunctionDeclaration(), /\\p\{L\}\\p\{N\}\\p\{Mn\}\\p\{Mc\}/u);
  assert.doesNotMatch(sourceReplayTokenDigestFunctionDeclaration(), /\\p\{M\}/u,
    "enclosing marks are not tokens in the native Rune categorizer");
  assert.match(sourceReplayTokenDigestFunctionDeclaration(), /simple, per-scalar mappings/u);

  const otherKeyDigester = await createDigester("cd".repeat(32));
  assert.notDeepEqual(await digestTokens(source, otherKeyDigester), actual,
    "per-run keys must prevent persisted digests from becoming reusable word fingerprints");
});

test("source link semantics retain image links with URL fragments and exclude only empty same-document anchors", () => {
  const readmeBase = "https://github.com/TheAlgorithms/Python/blob/0a72d14be62167935d5c18f762c68198d769f70a/README.md";

  assert.equal(shouldCountSourceReplayLink(
    "",
    "https://gitpod.io/#https://github.com/TheAlgorithms/Python",
    "https://gitpod.io/#https://github.com/TheAlgorithms/Python",
    readmeBase), true,
  "an image-only badge link to an external URL remains a distinct navigation target even when its URL has a fragment");
  assert.equal(shouldCountSourceReplayLink(
    "",
    "DIRECTORY.md#sorting",
    "https://github.com/TheAlgorithms/Python/blob/0a72d14be62167935d5c18f762c68198d769f70a/DIRECTORY.md#sorting",
    readmeBase), true,
  "a relative link to another document remains a distinct navigation target");
  assert.equal(shouldCountSourceReplayLink(
    "",
    "#getting-started",
    `${readmeBase}#getting-started`,
    readmeBase), false,
  "an empty same-document fragment anchor is a heading anchor rather than a user navigation destination");
  assert.equal(shouldCountSourceReplayLink(
    "",
    "#readme",
    `${readmeBase}#readme`,
    readmeBase,
    true), true,
  "a same-document fragment link around a non-decorative image is a named link destination");
  assert.equal(shouldCountSourceReplayLink(
    "",
    "#readme",
    `${readmeBase}#readme`,
    readmeBase,
    false), false,
  "an empty same-document anchor remains excluded when it has no accessible image name");
  assert.equal(shouldCountSourceReplayLink(
    "Section label",
    "#getting-started",
    `${readmeBase}#getting-started`,
    readmeBase), true,
  "a same-document fragment link with visible text remains a real link");
  assert.ok(sourceReplayFunctionDeclaration().includes(shouldCountSourceReplayLink.toString()),
    "the Edge-injected predicate must be the same implementation tested in Node");
});

test("same-byte browser traversals share a bounded overlapping viewport step", async () => {
  const replaySource = await readFile(new URL("./same-byte-edge-replay.mjs", import.meta.url), "utf8");
  const sourceReplaySource = await readFile(new URL("./same-byte-edge-source.mjs", import.meta.url), "utf8");

  assert.equal(SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO, 0.9);
  assert.equal(MAX_SAME_BYTE_TRAVERSAL_VIEWPORTS, 512);
  const viewSizePercent = 100 / 3;
  const requestedScrollPercent = viewSizePercent * SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO * 100 /
    (100 - viewSizePercent);
  const realizedViewportRatio = requestedScrollPercent / 100 *
    ((100 - viewSizePercent) / viewSizePercent);
  assert.ok(Math.abs(realizedViewportRatio - SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO) < 1e-12,
    "UIA's normalized scroll range must map back to the same 0.9-viewport physical step");
  assert.match(replaySource, /\{ value: SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO \}/u);
  assert.match(sourceReplaySource, /\{ value: nativeViewportProfile \}/u);
  assert.match(replaySource, /innerHeight \* viewportStepRatio/u);
  assert.match(sourceReplaySource, /innerHeight \* viewportStepRatio/u);
  assert.match(sourceReplaySource, /scrollTo\(0, expectedTop \* innerHeight\)/u);
  assert.match(sourceReplaySource,
    /expectedTop <= currentMaximumTop \+ positionTolerance && pass < 7[\s\S]*?scrollTo\(0, expectedTop \* innerHeight\);[\s\S]*?previousHeight = -1;/u,
    "reachable native movement targets are retried after image-induced scroll anchoring without relaxing the final position gate");
  assert.match(sourceReplaySource, /viewport\.movementOffsetsViewportUnits/u);
  assert.match(sourceReplaySource, /captureOffsetsViewportUnits/u);
  assert.match(sourceReplaySource, /fullTraversalMs - auditOnlyFrameWaitMs/u,
    "charged time is raw replay time minus measured proof-only frame waits");
  assert.match(sourceReplaySource, /passFrameWaitMs \+= await measuredNextFrame\(\)/u,
    "first movement frames are still observed and only a stable confirmation pass is excluded");
  assert.match(sourceReplaySource, /terminalProofFrameWaitMs/u,
    "only terminal proof frames are classified separately");
  assert.match(replaySource, /heightAfterPaint !== heightBeforePaint \|\| confirmedTop < confirmedMaxTop/u);
  assert.match(sourceReplaySource, /heightAfterPaint !== heightBeforePaint \|\| confirmedTop < confirmedMaxTop/u);
  assert.match(replaySource, /describeImageFailure\(visible\[failureIndex\]\)/u,
    "a failed visible decode must identify its captured route without losing the hard failure");
  assert.match(replaySource, /route=.*missing/u,
    "decode evidence must distinguish an uncaptured visible image from a captured one");
  assert.doesNotMatch(replaySource.slice(replaySource.indexOf("const describeImageFailure"),
    replaySource.indexOf("const waitVisibleImages")), /image\.src|image\.currentSrc|image\.alt/u,
    "decode diagnostics must not persist image URLs or alt text");
  assert.doesNotThrow(() => new Function(`return (${replayFunctionDeclaration()});`)());
});

test("source-bound Edge expression parses and inline image evidence is byte-bound", () => {
  const dataImage = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/3ZkAAAAASUVORK5CYII=";
  const htmlBytes = Buffer.from(
    `<article><img data-jithub-image-index="2" data-jithub-image-data="${dataImage}"></article>`,
    "utf8");
  const evidence = readCapturedInlineImageEvidence({
    imageRoutes: [{ index: 2, dataUri: true }],
    renderedHtmlBytes: htmlBytes,
    renderedHtmlSha256: sha256(htmlBytes),
  });
  assert.deepEqual(evidence, [{
    index: 2,
    mimeType: "image/png",
    sha256: sha256(Buffer.from(dataImage.slice(dataImage.indexOf(",") + 1), "base64")),
  }]);
  assert.throws(() => readCapturedInlineImageEvidence({
    imageRoutes: [{ index: 2, dataUri: true }],
    renderedHtmlBytes: htmlBytes,
    renderedHtmlSha256: "0".repeat(64),
  }), /missing or changed/u);
  assert.doesNotThrow(() => new Function(`return (${sourceReplayFunctionDeclaration()});`)());
});

test("source image identities claim captured routes one-to-one across aliases and repeated payloads", () => {
  const payloadA = "a".repeat(64);
  const payloadB = "b".repeat(64);
  const urlA = "1".repeat(64);
  const urlB = "2".repeat(64);
  const routes = [
    { index: 4, urlSha256: urlA, sha256: payloadA, mimeType: "image/png" },
    { index: 7, urlSha256: urlB, sha256: payloadA, mimeType: "image/png" },
    { index: 9, urlSha256: "3".repeat(64), sha256: payloadB, mimeType: "image/svg+xml" },
    { index: 12, dataUri: true },
  ];
  const matcher = createCapturedImageRouteIdentityMatcher(routes);
  assert.ok(matcher);
  assert.equal(matcher.routeCount, 4);
  assert.equal(matcher.claim({
    candidateUrlSha256s: [urlA],
    sha256: payloadA,
    mimeType: "image/png",
  }), 4, "an exact captured URL route wins over a byte-identical alias");
  assert.equal(matcher.claim({
    candidateUrlSha256s: ["4".repeat(64)],
    routeIndex: 7,
    sha256: payloadA,
    mimeType: "image/png",
  }), 7, "a unique captured alt identity can bridge a GitHub-rewritten URL");
  assert.equal(matcher.claim({
    candidateUrlSha256s: ["5".repeat(64)],
    sha256: payloadB,
    mimeType: "image/svg+xml",
  }), 9, "verified payload identity is the last URL-alias bridge");
  assert.equal(matcher.claim({
    routeIndex: 12,
    sha256: payloadB,
    mimeType: "image/png",
    dataUri: true,
  }), 12, "inline bytes bind to their exact captured route index");
  assert.equal(matcher.claimedCount, routes.length);
  assert.equal(matcher.isComplete(), true);

  const wrongPayload = createCapturedImageRouteIdentityMatcher(routes);
  assert.equal(wrongPayload.claim({
    candidateUrlSha256s: [urlA],
    sha256: payloadB,
    mimeType: "image/png",
  }), null, "matching URL hashes must not mask a payload identity mismatch");
  const wrongAltRoute = createCapturedImageRouteIdentityMatcher(routes);
  assert.equal(wrongAltRoute.claim({
    candidateUrlSha256s: [urlA],
    routeIndex: 7,
    sha256: payloadA,
    mimeType: "image/png",
  }), null, "a unique alt route must agree with any exact URL route identity");
  const overclaimed = createCapturedImageRouteIdentityMatcher([routes[0]]);
  assert.equal(overclaimed.claim({
    candidateUrlSha256s: [urlA],
    sha256: payloadA,
    mimeType: "image/png",
  }), 4);
  assert.equal(overclaimed.claim({
    candidateUrlSha256s: [urlA],
    sha256: payloadA,
    mimeType: "image/png",
  }), null, "duplicate source references cannot claim one captured route twice");
  assert.equal(overclaimed.isComplete(), true);
  assert.equal(createCapturedImageRouteIdentityMatcher([
    routes[0], { ...routes[0], index: 4 },
  ]), null, "duplicate captured route indices are rejected");
  assert.doesNotThrow(() => new Function("return (" + sourceReplayFunctionDeclaration() + ");")());
});

test("terminal Edge clamps require a stable physical bottom and retain exact replay evidence", () => {
  const viewportHeightPx = 611;
  const documentHeightPx = 22_169;
  const maximumOffsetViewportUnits = (documentHeightPx - viewportHeightPx) / viewportHeightPx;
  const requestedOffsetViewportUnits = 36.134;
  const positionTolerance = 1 / viewportHeightPx + 0.000001;
  const movement = createTerminalClampObservation({
    viewportIndex: 40,
    maximumViewportIndex: 511,
    movementIndex: 0,
    requestedOffsetViewportUnits,
    actualOffsetViewportUnits: maximumOffsetViewportUnits,
    documentHeightPx,
    viewportHeightPx,
    positionTolerance,
  });
  assert.ok(movement);
  assert.ok(requestedOffsetViewportUnits - movement.actualOffsetViewportUnits > 0.8,
    "a real page-extent mismatch is preserved, not hidden by pixel tolerance");
  assert.equal(movement.maximumOffsetViewportUnits, maximumOffsetViewportUnits);
  assert.equal(createTerminalClampObservation({
    viewportIndex: 40,
    maximumViewportIndex: 511,
    movementIndex: 0,
    requestedOffsetViewportUnits,
    actualOffsetViewportUnits: maximumOffsetViewportUnits - 0.01,
    documentHeightPx,
    viewportHeightPx,
    positionTolerance,
  }), null, "a stable offset away from Edge's actual bottom is never classified as a clamp");
  assert.equal(createTerminalClampObservation({
    viewportIndex: 40,
    maximumViewportIndex: 511,
    movementIndex: 0,
    requestedOffsetViewportUnits: maximumOffsetViewportUnits + positionTolerance / 2,
    actualOffsetViewportUnits: maximumOffsetViewportUnits,
    documentHeightPx,
    viewportHeightPx,
    positionTolerance,
  }), null, "pixel-tolerance matches do not produce clamp evidence");
  assert.equal(isTerminalBottomCaptureRepeat({
    terminalClampStarted: true,
    captureWasClamped: true,
    captureOffsetViewportUnits: maximumOffsetViewportUnits,
    previousCaptureOffsetViewportUnits: maximumOffsetViewportUnits,
    positionTolerance,
  }), true, "repeated bottom captures are allowed only after an evidenced terminal clamp");
  assert.equal(isTerminalBottomCaptureRepeat({
    terminalClampStarted: false,
    captureWasClamped: true,
    captureOffsetViewportUnits: maximumOffsetViewportUnits,
    previousCaptureOffsetViewportUnits: maximumOffsetViewportUnits,
    positionTolerance,
  }), false, "equal captures are not allowed as a general overlap exception");
  assert.equal(isTerminalBottomCaptureRepeat({
    terminalClampStarted: true,
    captureWasClamped: true,
    captureOffsetViewportUnits: maximumOffsetViewportUnits - 0.01,
    previousCaptureOffsetViewportUnits: maximumOffsetViewportUnits,
    positionTolerance,
  }), false, "a clamped suffix cannot move backward from Edge's terminal bottom");

  const capture = createTerminalClampObservation({
    viewportIndex: 41,
    maximumViewportIndex: 511,
    movementIndex: -1,
    requestedOffsetViewportUnits: 36.134,
    actualOffsetViewportUnits: maximumOffsetViewportUnits,
    documentHeightPx,
    viewportHeightPx,
    positionTolerance,
  });
  assert.ok(capture, "capture mismatches are explicitly identified with movementIndex -1");
  assert.deepEqual(sanitizeSourceReplayTerminalClampObservations([movement, capture]), [
    {
      viewportIndex: 40,
      movementIndex: 0,
      requestedOffsetViewportUnits: 36.134,
      actualOffsetViewportUnits: Number(maximumOffsetViewportUnits.toFixed(3)),
      maximumOffsetViewportUnits: Number(maximumOffsetViewportUnits.toFixed(3)),
      documentHeightPx,
      viewportHeightPx,
    },
    {
      viewportIndex: 41,
      movementIndex: -1,
      requestedOffsetViewportUnits: 36.134,
      actualOffsetViewportUnits: Number(maximumOffsetViewportUnits.toFixed(3)),
      maximumOffsetViewportUnits: Number(maximumOffsetViewportUnits.toFixed(3)),
      documentHeightPx,
      viewportHeightPx,
    },
  ]);
  assert.equal(sanitizeSourceReplayTerminalClampObservations([{ ...movement, documentHeightPx: 100_000_001 }]), null);
  assert.equal(sanitizeSourceReplayTerminalClampObservations(Array(512 * 18 + 1).fill(movement)), null);

  const replayFunction = sourceReplayFunctionDeclaration();
  assert.match(replayFunction, /terminalClampObservations/u);
  assert.doesNotMatch(replayFunction, /minimumTerminalViewportIndex/u,
    "terminal-clamp eligibility is proven by exact physical-bottom evidence, not an arbitrary viewport index");
  assert.match(replayFunction, /terminalClampStarted/u,
    "once a physical-bottom suffix starts, later offsets cannot leave it");
  assert.match(replayFunction, /attempted to leave its terminal bottom after a clamp/u,
    "a clamped traversal suffix cannot re-enter earlier Edge offsets");
  assert.match(replayFunction, /terminal-clamp evidence did not remain pinned to the final document bottom/u,
    "all observations must still describe the final Edge extent");
  assert.match(replayFunction, /terminalBottomCaptureRepeat/u,
    "zero-progress captures are accepted only for explicit clamped offsets at the same bottom");
});

test("native viewport profiles preserve captured overlap and every correction movement", () => {
  const profile = {
    schemaVersion: 1,
    viewportHeight: 600,
    viewports: [
      { captureOffsetViewportUnits: 0, movementOffsetsViewportUnits: [] },
      { captureOffsetViewportUnits: 0.89, movementOffsetsViewportUnits: [1.08, 0.89] },
      { captureOffsetViewportUnits: 1.78, movementOffsetsViewportUnits: [1.97, 1.78] },
    ],
  };
  assert.equal(validateNativeViewportProfile(profile, 600), true,
    "a native page-step may overshoot, then correct back to an overlapping capture offset");
  assert.equal(validateNativeViewportProfile(profile, 601), false,
    "profile geometry is bound to the native viewport height");
  assert.equal(validateNativeViewportProfile({
    ...profile,
    viewports: [profile.viewports[0], {
      captureOffsetViewportUnits: 1.01,
      movementOffsetsViewportUnits: [1.01],
    }],
  }, 600), false, "capture offsets with a coverage gap are rejected");
  assert.equal(validateNativeViewportProfile({
    ...profile,
    viewports: [profile.viewports[0], {
      captureOffsetViewportUnits: 0.89,
      movementOffsetsViewportUnits: [0.88],
    }],
  }, 600), false, "capture offsets must match the last native movement within one pixel");
  assert.equal(validateNativeViewportProfile({
    ...profile,
    viewports: [profile.viewports[0], {
      captureOffsetViewportUnits: 0.89,
      movementOffsetsViewportUnits: Array(18).fill(0.89),
    }],
  }, 600), false, "movement count is bounded to one page operation and its corrections");
});

test("rank-eight terminal clamp requires exact source identity and compatible full extents", () => {
  const nativeMaximum = (5214 - 611) / 611;
  const edgeMaximum = (4850 - 611) / 611;
  const profile = {
    schemaVersion: 1,
    viewportHeight: 611,
    readmeGitBlobSha1: "a".repeat(40),
    readmeSha256: "b".repeat(64),
    readmeByteSize: 2550,
    decodedTextSha256: "c".repeat(64),
    nativeEstimatedContentHeightPx: 5214,
    viewports: Array.from({ length: 10 }, (_, index) => ({
      captureOffsetViewportUnits: index === 9 ? nativeMaximum : index * 0.9,
      movementOffsetsViewportUnits: index === 0 ? [] : [index === 9 ? nativeMaximum : index * 0.9],
    })),
  };
  const exactSource = {
    readmeGitBlobSha1: profile.readmeGitBlobSha1,
    readmeSha256: profile.readmeSha256,
    byteSize: profile.readmeByteSize,
  };
  assert.equal(validateNativeViewportProfile(profile, 611, exactSource), true,
    "the profile's immutable source identity matches the captured Edge corpus");
  assert.equal(profile.viewports.length, 10);
  assert.ok(profile.viewports[7].captureOffsetViewportUnits < edgeMaximum);
  assert.equal(profile.viewports[8].captureOffsetViewportUnits, 7.2);
  assert.ok(profile.viewports[8].captureOffsetViewportUnits > edgeMaximum + 1 / 611 + 0.000001,
    "the first Edge clamp occurs at native viewport index eight, before an arbitrary 90% index cutoff");
  assert.equal(profile.viewports[9].captureOffsetViewportUnits, nativeMaximum);
  assert.equal(validateNativeViewportProfile(profile, 611, {
    ...exactSource,
    readmeGitBlobSha1: "d".repeat(40),
  }), false, "a profile from a different Git README blob is rejected");
  assert.equal(isSourceReplayExtentCompatible(5214, 4850, 611), true,
    "the rank-eight clamp shape remains inside the existing 0.90–1.10 full-extent envelope");
  assert.equal(isSourceReplayExtentCompatible(5214, 4300, 611), false,
    "physical bottom alone cannot excuse an extent mismatch above ten percent");
  assert.equal(isSourceReplayExtentCompatible(5214, 4740, 611), true,
    "the 0.90 edge ratio boundary remains accepted exactly");
  assert.equal(isSourceReplayExtentCompatible(5214, 4739, 611), false,
    "the lower edge extent boundary is not rounded outward");
  assert.equal(isSourceReplayExtentCompatible(5214, 5793, 611), true,
    "the 1.10 edge ratio boundary remains accepted exactly");
  assert.equal(isSourceReplayExtentCompatible(5214, 5794, 611), false,
    "the upper edge extent boundary is not rounded outward");
});

test("Edge capture offsets allow endpoint pixel rounding while rejecting a material gap", () => {
  const viewportHeight = 611;
  const positionTolerance = 1 / viewportHeight + 0.000001;
  const roundedEndpoints = validateNativeCaptureObservation(
    10.9 + positionTolerance,
    10.9,
    10 - positionTolerance,
    positionTolerance,
    SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO,
  );
  assert.deepEqual(roundedEndpoints, { positionMatches: true, stepPreservesOverlap: true });
  assert.ok(0.9 + 2 * positionTolerance < 1,
    "two endpoint pixels are below one viewport and retain overlapping coverage");

  const materialGap = validateNativeCaptureObservation(
    11.01,
    10.9,
    10,
    positionTolerance,
    SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO,
  );
  assert.deepEqual(materialGap, { positionMatches: false, stepPreservesOverlap: false });
  assert.doesNotThrow(() => new Function(`return (${sourceReplayFunctionDeclaration()});`)());
});

test("source-only Edge tail is captured separately with the unchanged overlapping step", () => {
  const viewportHeight = 611;
  const nativeTerminalOffset = 21.039279869;
  const sourceTerminalOffset = 21.429;
  const positionTolerance = 1 / viewportHeight + 0.000001;
  const tailTarget = nextSourceTailOffset(
    nativeTerminalOffset,
    sourceTerminalOffset,
    SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO,
  );
  const tailDelta = tailTarget - nativeTerminalOffset;
  const validation = validateNativeCaptureObservation(
    tailTarget,
    sourceTerminalOffset,
    nativeTerminalOffset,
    positionTolerance,
    SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO,
  );

  assert.equal(SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO, 0.9,
    "the source-only tail keeps the original requested viewport step");
  assert.equal(tailTarget, sourceTerminalOffset,
    "the tail pass reaches the independently measured Edge bottom");
  assert.ok(Math.abs(tailDelta - 0.389720131) < 1e-12);
  assert.deepEqual(validation, { positionMatches: true, stepPreservesOverlap: true });
  assert.ok(tailDelta < 1,
    "the source-tail viewport overlaps the preceding native-aligned capture");
  assert.ok(Math.abs((1 - tailDelta) * viewportHeight - 372.881) < 0.001,
    "the remaining overlap is preserved in viewport pixels");
  assert.equal(nextSourceTailOffset(1.2, 3, 0.9), 2.1,
    "longer tails advance in bounded 0.9-viewport increments");
  assert.equal(nextSourceTailOffset(3, 3, 0.9), null,
    "no extra source-tail viewport is invented when the native profile already reaches the source bottom");
  assert.throws(() => nextSourceTailOffset(0, 2, 1), /invalid bounded offsets/u);

  const sourceReplaySource = sourceReplayFunctionDeclaration();
  assert.match(sourceReplaySource, /sourceTailCaptureOffsetsViewportUnits/u);
  assert.match(sourceReplaySource, /sourceTailMovementCount\+\+/u);
  assert.match(sourceReplaySource, /nextSourceTailOffset\(actualTop, maxTop, viewportStepRatio\)/u);
  assert.match(sourceReplaySource, /stepPreservesOverlap/u);
  assert.match(sourceReplaySource, /traversalViewportCount \+ sourceTailViewportCount >= maximumSteps/u);
  assert.doesNotMatch(sourceReplaySource, /final native capture must also be the confirmed source bottom/u);
});

test("source-bound replay failure reports keep status and category without paths or URLs", () => {
  const checkoutPath = String.raw`C:\agent\_work\JitHubV2\eng\readme-audit\same-byte-edge-source.mjs`;
  const sourceUrl = "https://private.example.invalid/org/repository/README.md?token=secret";
  const report = createSourceReplayFailureReport(
    new Error(`Blocked external resource ${sourceUrl} while running ${checkoutPath}`));

  assert.equal(report.status, "failed");
  assert.equal(report.failureCategory, "external-resource");
  assert.equal(report.error, "The Markdown replay attempted a disallowed external resource.");
  const serialized = JSON.stringify(report);
  assert.doesNotMatch(serialized, /https?:\/\//iu);
  assert.doesNotMatch(serialized, /C:\\agent|JitHubV2|README\.md|token=secret/iu);
});

test("native movement failures retain only bounded numeric diagnostics and remain failed", () => {
  const replayFunction = sourceReplayFunctionDeclaration();
  assert.match(replayFunction, /const failNativeMovement = \(message, viewportIndex, movementIndex, requestedOffset, actualOffset\)/u);
  assert.match(replayFunction, /nativeMovementEvidence: failureStage === "native-movement"/u);

  const rawEvidence = {
    viewportIndex: 4,
    movementIndex: 0,
    requestedOffsetViewportUnits: 7.509018,
    actualOffsetViewportUnits: 7.238477,
    maximumOffsetViewportUnits: 7.238477,
    documentHeightPx: 4108,
    viewportHeightPx: 499,
    repositoryUrl: "https://private.example.invalid/org/repository",
    readmeText: "private README contents",
  };
  const safeEvidence = {
    viewportIndex: 4,
    movementIndex: 0,
    requestedOffsetViewportUnits: 7.509,
    actualOffsetViewportUnits: 7.238,
    maximumOffsetViewportUnits: 7.238,
    documentHeightPx: 4108,
    viewportHeightPx: 499,
  };
  assert.deepEqual(sanitizeSourceReplayNativeMovementEvidence(rawEvidence), safeEvidence);

  const failure = new Error("private README text https://private.example.invalid/secret.png");
  Object.defineProperties(failure, {
    sourceReplayStage: { value: "native-movement" },
    sourceReplayNativeMovementEvidence: { value: rawEvidence },
  });
  const report = createSourceReplayFailureReport(failure);
  assert.equal(report.status, "failed");
  assert.equal(report.failureCategory, "replay");
  assert.equal(report.failureStage, "native-movement");
  assert.deepEqual(report.nativeMovementEvidence, safeEvidence);
  const serialized = JSON.stringify(report);
  assert.doesNotMatch(serialized, /https?:\/\/|private README|repositoryUrl|readmeText/iu);

  const unbounded = new Error("movement did not settle");
  Object.defineProperties(unbounded, {
    sourceReplayStage: { value: "native-movement" },
    sourceReplayNativeMovementEvidence: {
      value: { ...rawEvidence, viewportIndex: Number.MAX_SAFE_INTEGER },
    },
  });
  const unboundedReport = createSourceReplayFailureReport(unbounded);
  assert.equal(unboundedReport.status, "failed");
  assert.equal("nativeMovementEvidence" in unboundedReport, false);

  const wrongStage = new Error("movement did not settle");
  Object.defineProperties(wrongStage, {
    sourceReplayStage: { value: "native-capture-position" },
    sourceReplayNativeMovementEvidence: { value: rawEvidence },
  });
  assert.equal("nativeMovementEvidence" in createSourceReplayFailureReport(wrongStage), false);
});

test("source replay failure predicates are allowlisted and stage-bound", () => {
  for (const [stage, predicate] of [
    ["sanitize-markdown", "invalid-image-dimension"],
    ["sanitize-markdown", "responsive-image-candidates"],
    ["sanitize-markdown", "sanitized-image-count"],
    ["image-map", "conflicting-image-aliases"],
    ["image-map", "invalid-image-url"],
    ["image-map", "invalid-inline-image-data"],
    ["image-map", "inline-image-byte-mismatch"],
    ["image-map", "missing-captured-image-alias"],
    ["image-map", "captured-image-route-mismatch"],
    ["image-map", "unsupported-image-url-scheme"],
    ["inline-image-evidence", "unmatched-inline-image-evidence"],
    ["inline-image-evidence", "captured-image-route-mismatch"],
  ]) {
    const supported = new Error("private source detail");
    Object.defineProperties(supported, {
      sourceReplayStage: { value: stage },
      sourceReplayFailurePredicate: { value: predicate },
    });
    const report = createSourceReplayFailureReport(supported);
    assert.equal(report.failureStage, stage);
    assert.equal(report.failurePredicate, predicate);
    assert.doesNotMatch(JSON.stringify(report), /private source detail/iu);
  }

  const unsupported = new Error("private source detail");
  Object.defineProperties(unsupported, {
    sourceReplayStage: { value: "sanitize-markdown" },
    sourceReplayFailurePredicate: { value: "raw-html-value=https://private.example.invalid" },
  });
  assert.equal("failurePredicate" in createSourceReplayFailureReport(unsupported), false);

  const missingAlias = new Error("A captured Markdown image could not be verified or decoded.");
  Object.defineProperties(missingAlias, {
    sourceReplayStage: { value: "image-map" },
    sourceReplayFailurePredicate: { value: "missing-captured-image-alias" },
    sourceReplayImageMapEvidence: {
      value: {
        imageIndex: 3,
        candidateUrlSha256s: ["A".repeat(64), "invalid", "b".repeat(64)],
        capturedAssetUrlEntryCount: 19,
        sourceRouteIndex: 4,
        routeCandidates: [
          {
            routeIndex: 4,
            urlSha256: "C".repeat(64),
            payloadSha256: "D".repeat(64),
            mimeType: "image/png",
            rawUrl: "https://private.example.invalid/captured.png",
          },
          {
            routeIndex: -1,
            urlSha256: "E".repeat(64),
            payloadSha256: "F".repeat(64),
            mimeType: "image/png",
          },
          {
            routeIndex: 5,
            urlSha256: "9".repeat(64),
            payloadSha256: "8".repeat(64),
            mimeType: "application/octet-stream",
          },
        ],
        rawUrl: "https://private.example.invalid/image.png?token=secret",
      },
    },
  });
  const missingAliasReport = createSourceReplayFailureReport(missingAlias);
  assert.deepEqual(missingAliasReport.imageMapEvidence, {
    imageIndex: 3,
    candidateUrlSha256s: ["a".repeat(64), "b".repeat(64)],
    capturedAssetUrlEntryCount: 19,
    sourceRouteIndex: 4,
    routeCandidates: [{
      routeIndex: 4,
      urlSha256: "c".repeat(64),
      payloadSha256: "d".repeat(64),
      mimeType: "image/png",
    }, {
      routeIndex: 5,
      urlSha256: "9".repeat(64),
    }],
  });
  assert.doesNotMatch(JSON.stringify(missingAliasReport), /https?:\/\/|private\.example|private source detail/iu);

  const wrongStage = new Error("private source detail");
  Object.defineProperties(wrongStage, {
    sourceReplayStage: { value: "image-map" },
    sourceReplayFailurePredicate: { value: "invalid-image-dimension" },
  });
  assert.equal("failurePredicate" in createSourceReplayFailureReport(wrongStage), false);
});

test("extent mismatch evidence records bounded heights and the exact Edge style fingerprint", () => {
  const error = new Error("private replay details must not enter failure evidence");
  Object.defineProperties(error, {
    sourceReplayStage: { value: "native-terminal-profile-bottom" },
    sourceReplayExtentEvidence: {
      value: {
        nativeEstimatedContentHeightPx: 5214,
        edgeRenderedContentHeightPx: 4650,
        viewportHeightPx: 611,
        styleProfileId: PINNED_GITHUB_README_STYLE_PROFILE.id,
        styleProfileVersion: PINNED_GITHUB_README_STYLE_PROFILE.version,
        styleProfileSha256: PINNED_GITHUB_README_STYLE_PROFILE.sha256,
        rawUrl: "https://private.example.invalid/readme",
      },
    },
  });
  const report = createSourceReplayFailureReport(error);
  assert.deepEqual(report.extentEvidence, {
    nativeEstimatedContentHeightPx: 5214,
    edgeRenderedContentHeightPx: 4650,
    viewportHeightPx: 611,
    nativeToEdgeExtentRatio: 1.12129,
    styleProfileId: PINNED_GITHUB_README_STYLE_PROFILE.id,
    styleProfileVersion: PINNED_GITHUB_README_STYLE_PROFILE.version,
    styleProfileSha256: PINNED_GITHUB_README_STYLE_PROFILE.sha256,
  });
  assert.doesNotMatch(JSON.stringify(report), /https?:\/\/|private\.example|private replay details/iu);

  const wrongFingerprint = new Error("extent mismatch");
  Object.defineProperties(wrongFingerprint, {
    sourceReplayStage: { value: "native-terminal-profile-bottom" },
    sourceReplayExtentEvidence: {
      value: {
        nativeEstimatedContentHeightPx: 5214,
        edgeRenderedContentHeightPx: 4650,
        viewportHeightPx: 611,
        styleProfileId: PINNED_GITHUB_README_STYLE_PROFILE.id,
        styleProfileVersion: PINNED_GITHUB_README_STYLE_PROFILE.version,
        styleProfileSha256: "0".repeat(64),
      },
    },
  });
  assert.equal("extentEvidence" in createSourceReplayFailureReport(wrongFingerprint), false,
    "extent evidence must be bound to the known canonical stylesheet fingerprint");
});

test("semantic image fallback maps only individually unique alt identities", () => {
  const first = "1".repeat(64);
  const second = "2".repeat(64);
  const third = "3".repeat(64);
  const mapping = createUniqueCapturedImageAltRouteMap(
    [first, second],
    [{ index: 7, altSha256: second }, { index: 2, altSha256: first }],
    2,
  );
  assert.deepEqual([...mapping.entries()], [[first, 2], [second, 7]]);
  assert.equal(createUniqueCapturedImageAltRouteMap(
    [first, first],
    [{ index: 2, altSha256: first }, { index: 7, altSha256: second }],
    2,
  ), null, "duplicate source alts cannot establish identity");
  assert.equal(createUniqueCapturedImageAltRouteMap(
    [first, second],
    [{ index: 2, altSha256: first }, { index: 7, altSha256: first }],
    2,
  ), null, "duplicate captured alts cannot establish identity");
  assert.equal(createUniqueCapturedImageAltRouteMap(
    [first], [{ index: 2, altSha256: first }, { index: 7, altSha256: second }], 2,
  ), null, "count mismatches cannot fall back by ordinal");
  assert.deepEqual([...createUniqueCapturedImageAltRouteMap(
    [first, second], [{ index: 2, altSha256: first }, { index: 7, altSha256: third }], 2,
  ).entries()], [[first, 2]], "a nonmatching alt cannot fall back by ordinal");
  assert.deepEqual([...createUniqueCapturedImageAltRouteMap(
    [first, null], [{ index: 2, altSha256: first }, { index: 7, altSha256: second }], 2,
  ).entries()], [[first, 2]], "a missing alt cannot suppress a different image's unique identity");
  assert.deepEqual([...createUniqueCapturedImageAltRouteMap(
    [first, second, second, null],
    [{ index: 7, altSha256: second }, { index: 2, altSha256: first },
      { index: 3, altSha256: second }, { index: 4, altSha256: null }],
    4,
  ).entries()], [[first, 2]], "duplicate badge alts must not suppress a distinct screenshot alias");
});

test("visible-image decode failures retain only bounded route hashes and decode outcomes", () => {
  const error = new Error("A visible pinned Markdown image did not decode successfully.");
  Object.defineProperties(error, {
    sourceReplayStage: { value: "visible-image-decode" },
    sourceReplayImageDecodeEvidence: {
      value: {
        visibleImageCount: 2,
        failedVisibleImageCount: 2,
        images: [
          {
            imageIndex: 4,
            capturedUrlSha256: "A".repeat(64),
            dataUri: false,
            ready: false,
            decodeOutcome: "load-error",
            rawUrl: "https://private.example.invalid/image.png?token=secret",
          },
          {
            imageIndex: 7,
            dataUri: true,
            ready: false,
            decodeOutcome: "decode-rejected",
            localPath: "C:\\private\\checkout\\readme.md",
          },
        ],
      },
    },
  });

  const report = createSourceReplayFailureReport(error);
  assert.equal(report.failureCategory, "image");
  assert.equal(report.failureStage, "visible-image-decode");
  assert.deepEqual(report.imageDecodeEvidence, {
    visibleImageCount: 2,
    failedVisibleImageCount: 2,
    images: [
      {
        imageIndex: 4,
        capturedUrlSha256: "a".repeat(64),
        dataUri: false,
        ready: false,
        decodeOutcome: "load-error",
      },
      {
        imageIndex: 7,
        dataUri: true,
        ready: false,
        decodeOutcome: "decode-rejected",
      },
    ],
    omittedVisibleImageCount: 0,
  });
  const serialized = JSON.stringify(report);
  assert.doesNotMatch(serialized, /https?:\/\/|private\.example|checkout|readme\.md|token=secret/iu);
});

test("source-bound replay failures retain only an allowlisted stage and error type", async () => {
  const cdp = {
    on() { return () => {}; },
    async send(method) {
      if (method === "Page.navigate") {
        throw new TypeError(String.raw`failed at C:\private\checkout https://private.example.invalid/?token=secret`);
      }
      throw new Error("cleanup failure");
    },
  };
  const replayServer = {
    baseUrl: "http://127.0.0.1:43210",
    manifest: {
      browserRender: { status: "passed" },
      imageRoutes: [],
      repository: {
        fullName: "example/repo",
        readmePath: "README.md",
        commitSha: "0123456789012345678901234567890123456789",
      },
    },
    readmeBytes: Buffer.from("captured source"),
    renderedHtmlBytes: Buffer.from("<article>captured</article>"),
    renderedHtmlSha256: sha256(Buffer.from("<article>captured</article>")),
    imageRoutes: [],
    parser: { ...PINNED_SOURCE_REPLAY_PARSER },
    styleProfile: { ...PINNED_GITHUB_README_STYLE_PROFILE },
  };
  let caught;
  await assert.rejects(replaySourceBoundMarkdownInEdge({
    cdp,
    replayServer,
    outputDirectory: "unused-output",
    viewport: { width: 640, height: 480, deviceScaleFactor: 1 },
    semanticDigestKey: "ab".repeat(32),
  }), error => {
    caught = error;
    assert.equal(error.sourceReplayStage, "reset-document");
    return true;
  });

  const report = createSourceReplayFailureReport(caught);
  assert.equal(report.status, "failed");
  assert.equal(report.failureCategory, "replay");
  assert.equal(report.failureStage, "reset-document");
  assert.equal(report.errorType, "TypeError");
  assert.equal(report.error, "The source-bound Markdown replay failed.");
  const serialized = JSON.stringify(report);
  assert.doesNotMatch(serialized, /https?:\/\/|C:\\private|checkout|token=secret/iu);
  assert.doesNotMatch(serialized, /failed at|cleanup failure/iu);
});

test("source page failures expose fixed substage and type without persisting exception text", async () => {
  const cdp = {
    on() { return () => {}; },
    async send(method, params = {}) {
      if (method === "Runtime.evaluate") {
        return params.expression === "window"
          ? { result: { objectId: "window" } }
          : { result: { value: true } };
      }
      if (method === "Runtime.callFunctionOn") {
        return {
          result: { value: {
            ok: false,
            failureStage: "native-terminal-source-bottom",
            errorType: "TypeError",
            error: String.raw`failed at C:\private\checkout https://private.example.invalid/?token=secret`,
          } },
        };
      }
      return {};
    },
  };
  const replayServer = {
    baseUrl: "http://127.0.0.1:43210",
    manifest: {
      browserRender: { status: "passed" },
      imageRoutes: [],
      repository: {
        fullName: "example/repo",
        readmePath: "README.md",
        commitSha: "0123456789012345678901234567890123456789",
      },
    },
    readmeBytes: Buffer.from("captured source"),
    renderedHtmlBytes: Buffer.from("<article>captured</article>"),
    renderedHtmlSha256: sha256(Buffer.from("<article>captured</article>")),
    imageRoutes: [],
    parser: { ...PINNED_SOURCE_REPLAY_PARSER },
    styleProfile: { ...PINNED_GITHUB_README_STYLE_PROFILE },
  };
  let caught;
  await assert.rejects(replaySourceBoundMarkdownInEdge({
    cdp,
    replayServer,
    outputDirectory: "unused-output",
    viewport: { width: 640, height: 480, deviceScaleFactor: 1 },
    semanticDigestKey: "ab".repeat(32),
  }), error => {
    caught = error;
    assert.equal(error.sourceReplayStage, "native-terminal-source-bottom");
    assert.equal(error.sourceReplayErrorType, "TypeError");
    return true;
  });

  const report = createSourceReplayFailureReport(caught);
  assert.equal(report.failureStage, "native-terminal-source-bottom");
  assert.equal(report.errorType, "TypeError");
  assert.equal(report.error, "The source-bound Markdown replay failed.");
  const serialized = JSON.stringify(report);
  assert.doesNotMatch(serialized, /https?:\/\/|C:\\private|checkout|token=secret/iu);
  assert.doesNotMatch(serialized, /failed at/iu);
});

test("source-bound Edge rendered extent requires gap-free exact tile coverage", () => {
  assert.doesNotThrow(() => validateTileCoverage([
    { relativeY: 4, width: 80, height: 6 },
    { relativeY: 0, width: 80, height: 6 },
  ], 80, 10));
  assert.throws(() => validateTileCoverage([
    { relativeY: 0, width: 80, height: 4 },
    { relativeY: 5, width: 80, height: 5 },
  ], 80, 10), /continuously cover/u);
  assert.throws(() => validateTileCoverage([
    { relativeY: 0, width: 79, height: 10 },
  ], 80, 10), /continuously cover/u);
  assert.throws(() => validateTileCoverage([
    { relativeY: 0, width: 80, height: 9 },
  ], 80, 10), /reach the end/u);
});

const edgePath = [
  path.join(process.env["ProgramFiles(x86)"] || "", "Microsoft", "Edge", "Application", "msedge.exe"),
  path.join(process.env.ProgramFiles || "", "Microsoft", "Edge", "Application", "msedge.exe"),
].find(candidate => candidate && existsSync(candidate));

test("Edge same-byte replay renders from loopback bytes with client-only timing", {
  skip: !edgePath ? "Microsoft Edge is not installed on this test host." : false,
}, async t => {
  const corpusDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-edge-corpus-"));
  const outputDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-edge-output-"));
  const profileDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-readme-edge-"));
  let replayServer;
  let sourceReplayServer;
  let profileReplayServer;
  let edge;
  let cdp;
  t.after(async () => {
    try {
      if (cdp) await Promise.race([
        cdp.send("Browser.close"),
        new Promise(resolve => setTimeout(resolve, 1000)),
      ]);
    } catch {}
    try { cdp?.close(); } catch {}
    try { edge?.kill(); } catch {}
    await cleanupBrowserProfile(profileDirectory, {
      closeServers: async () => {
        let closeFailed = false;
        for (const server of [profileReplayServer, sourceReplayServer, replayServer]) {
          if (!server) continue;
          try { await server.close(); } catch { closeFailed = true; }
        }
        if (closeFailed) throw new Error("A same-byte replay server did not confirm closure.");
      },
    });
    await rm(corpusDirectory, { recursive: true, force: true });
    await rm(outputDirectory, { recursive: true, force: true });
  });

  const dataImage = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/3ZkAAAAASUVORK5CYII=";
  const pictureCandidateSourceUrl = "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/picture-selected.gif";
  // The pinned rank-2 README shape has multiple responsive source candidates
  // inside one picture. Drop the unsupported candidates but retain its img fallback.
  const pinnedEmoji = await loadPinnedGitHubEmojiShortcodes();
  const emojiFixtureAlias = Object.keys(pinnedEmoji.aliases)[0];
  const emojiFixtureValue = pinnedEmoji.aliases[emojiFixtureAlias];
  const traversalParagraphs = `Emoji shortcode :${emojiFixtureAlias}: must match GitHub's source semantics.\n\n` +
    Array.from({ length: 80 }, (_, index) =>
    `Traversal paragraph ${index + 1} keeps the pinned Markdown article taller than several viewports.`).join("\n\n") +
    "\n\n<picture><source media=\"(min-width: 1px)\" srcset=\"https://untrusted.invalid/should-not-load.svg\"><source media=\"(min-width: 2px)\" srcset=\"https://untrusted.invalid/also-should-not-load.svg\"><img src=\"picture.svg\" alt=\"picture\" height=\"100px\"></picture>" +
    `\n\n<picture><source type="image/gif" srcset="${pictureCandidateSourceUrl}"><img src="unselected-poster.gif" alt="source-selected" width="32" height="24"></picture>` +
    `\n\n<picture><source type="image/gif" srcset="${pictureCandidateSourceUrl} 1x, ${pictureCandidateSourceUrl}?variant=2 2x"><img src="picture.svg" alt="ambiguous-fallback"></picture>` +
    "\n\n<img src=\"picture.svg\" alt=\"wide\" width=\"1000px\" height=\"500px\">";
  const readmeBytes = Buffer.from(
    `[![offline](image.svg)](#readme)\n\n${traversalParagraphs}\n\n![deferred](bottom.svg)\n\n![inline](${dataImage})\n\n<details><summary>collapsed</summary><img src=\"not-captured.svg\" alt=\"hidden\" width=\"80%\"><img src=\"not-captured2.svg\" alt=\"hidden-legacy\" width=\"60px;\"></details>\n`,
    "utf8");
  const imageBytes = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="2" height="2"><rect width="2" height="2" fill="red"/></svg>', "utf8");
  const bottomImageBytes = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="2" height="2"><rect width="2" height="2" fill="blue"/></svg>', "utf8");
  const pictureImageBytes = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="2" height="2"><rect width="2" height="2" fill="green"/></svg>', "utf8");
  const pictureCandidateImageBytes = Buffer.from("R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs=", "base64");
  const sourceUrl = "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/image.svg";
  const bottomSourceUrl = "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/bottom.svg";
  const pictureSourceUrl = "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/picture.svg";
  const pictureCandidateImageSha256 = sha256(pictureCandidateImageBytes);
  const imageSha256 = sha256(imageBytes);
  const bottomImageSha256 = sha256(bottomImageBytes);
  const pictureImageSha256 = sha256(pictureImageBytes);
  const readmeSha256 = sha256(readmeBytes);
  const capturedImageUrlSha256 = resourceUrlSha256(
    "https://github.com/example/repo/blob/0123456789012345678901234567890123456789/captured/image.svg?raw=1");
  const traversalHtml = Array.from({ length: 80 }, (_, index) =>
    `<p>Traversal paragraph ${index + 1} keeps the captured article taller than several viewports.</p>`).join("");
  const html = `<article class="markdown-body" style="width:320px;color:#222;font:14px Arial"><p>offline replay</p><img alt="offline" width="2" height="2" data-jithub-image-index="0">${traversalHtml}<img alt="picture" width="100" height="100" data-jithub-image-index="1"><img alt="wide" width="1000" height="500" data-jithub-image-index="2"><img alt="deferred" width="2" height="2" data-jithub-image-index="3"><img alt="inline" data-jithub-image-index="4" data-jithub-image-data="${dataImage}"><img alt="source-selected" width="32" height="24" data-jithub-image-index="5"><img alt="ambiguous-fallback" data-jithub-image-index="6"></article>`;
  const htmlBytes = Buffer.from(html, "utf8");
  const bottomUrlSha256 = resourceUrlSha256(bottomSourceUrl);
  const pictureUrlSha256 = resourceUrlSha256(pictureSourceUrl);
  const pictureCandidateUrlSha256 = resourceUrlSha256(pictureCandidateSourceUrl);
  const manifest = {
    schemaVersion: 2,
    complete: true,
    repository: {
      fullName: "example/repo",
      commitSha: "0123456789012345678901234567890123456789",
      readmePath: "README.md",
      readmeGitBlobSha1: gitBlobSha1(readmeBytes),
    },
    readme: { file: "readme.md", byteSize: readmeBytes.length, sha256: readmeSha256 },
    assets: [
      { urlSha256: capturedImageUrlSha256, sha256: imageSha256, byteSize: imageBytes.length, mimeType: "image/svg+xml" },
      { urlSha256: bottomUrlSha256, sha256: bottomImageSha256, byteSize: bottomImageBytes.length, mimeType: "image/svg+xml" },
      { urlSha256: pictureUrlSha256, sha256: pictureImageSha256, byteSize: pictureImageBytes.length, mimeType: "image/svg+xml" },
      { urlSha256: pictureCandidateUrlSha256, sha256: pictureCandidateImageSha256, byteSize: pictureCandidateImageBytes.length, mimeType: "image/gif" },
    ],
    imageRoutes: [
      { index: 0, urlSha256: capturedImageUrlSha256, sha256: imageSha256, mimeType: "image/svg+xml" },
      { index: 1, urlSha256: pictureUrlSha256, sha256: pictureImageSha256, mimeType: "image/svg+xml" },
      { index: 2, urlSha256: pictureUrlSha256, sha256: pictureImageSha256, mimeType: "image/svg+xml" },
      { index: 3, urlSha256: bottomUrlSha256, sha256: bottomImageSha256, mimeType: "image/svg+xml" },
      { index: 4, dataUri: true },
      { index: 5, urlSha256: pictureCandidateUrlSha256, sha256: pictureCandidateImageSha256, mimeType: "image/gif" },
      { index: 6, urlSha256: pictureUrlSha256, sha256: pictureImageSha256, mimeType: "image/svg+xml" },
    ],
    browserRender: { status: "passed", file: "rendered.html", byteSize: htmlBytes.length, sha256: sha256(htmlBytes) },
    limits: {
      maxReadmeBytes: 16 * 1024 * 1024,
      maxRenderedHtmlBytes: 32 * 1024 * 1024,
      maxAssetBytes: 64 * 1024 * 1024,
      maxAggregateAssetBytes: 256 * 1024 * 1024,
    },
  };
  await mkdir(path.join(corpusDirectory, "assets"));
  await writeFile(path.join(corpusDirectory, "readme.md"), readmeBytes);
  await writeFile(path.join(corpusDirectory, "rendered.html"), htmlBytes);
  await writeFile(path.join(corpusDirectory, "assets", imageSha256), imageBytes);
  await writeFile(path.join(corpusDirectory, "assets", bottomImageSha256), bottomImageBytes);
  await writeFile(path.join(corpusDirectory, "assets", pictureImageSha256), pictureImageBytes);
  await writeFile(path.join(corpusDirectory, "assets", pictureCandidateImageSha256), pictureCandidateImageBytes);
  await writeFile(path.join(corpusDirectory, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);

  replayServer = await createSameByteReplayServer(corpusDirectory);
  sourceReplayServer = await createSameByteReplayServer(corpusDirectory);

  edge = spawn(edgePath, [
    "--headless=new",
    "--no-first-run",
    "--no-default-browser-check",
    "--disable-background-networking",
    "--disable-component-update",
    "--disable-sync",
    "--remote-debugging-port=0",
    `--user-data-dir=${profileDirectory}`,
    "about:blank",
  ], { stdio: "ignore", windowsHide: true });
  const portFile = path.join(profileDirectory, "DevToolsActivePort");
  const portContent = await waitForDevToolsPort(portFile, edge, () => "", 20_000);
  const port = Number(portContent.split(/\r?\n/u, 1)[0]);
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const target = targets.find(item => item.type === "page");
  assert.ok(target?.webSocketDebuggerUrl, "Edge should expose a page WebSocket");
  cdp = await connectCdp(target.webSocketDebuggerUrl);
  await Promise.all([
    cdp.send("Page.enable"),
    cdp.send("Runtime.enable"),
    cdp.send("Network.enable"),
    cdp.send("Emulation.setDeviceMetricsOverride", {
      width: 640,
      height: 480,
      deviceScaleFactor: 1,
      mobile: false,
    }),
  ]);

  // Execute the exact dynamic expression used by browser-oracle, not a copied
  // approximation. The production expression lives in a Node template literal
  // and is then sent to Edge Runtime.evaluate, so its escape processing must be
  // exercised before the loopback replay navigation replaces this document.
  await cdp.send("Runtime.evaluate", {
    expression: `document.body.innerHTML = ${JSON.stringify(`<div id="readme"><article class="markdown-body"><p>snapshot syntax</p><img alt="inline" src="${dataImage}"></article></div>`)}`,
    returnByValue: true,
  });
  const snapshotExpression = await readRenderedSnapshotExpression();
  const snapshotResult = await cdp.send("Runtime.evaluate", {
    expression: snapshotExpression,
    returnByValue: true,
  });
  assert.equal(snapshotResult.exceptionDetails, undefined,
    snapshotResult.exceptionDetails?.exception?.description || snapshotResult.exceptionDetails?.text);
  const capturedHtml = snapshotResult.result?.value;
  assert.match(capturedHtml, /data-jithub-image-data="data:image\/png;base64,/iu);
  assert.doesNotMatch(capturedHtml, /\ssrc=/iu);

  const unsupportedMarkupExamples = {
    githubStrippedForm: '<article><p>Search documentation</p></article>',
    githubRetainedVideo: '<article><video style="display:block;width:20px;height:20px"></video></article>',
    hiddenVideo: '<article><div style="display:none"><video></video></div></article>',
    collapsedVideo: '<article><details><summary>More</summary><video></video></details></article>',
    visibleSourceForm: '<article><form><input type="search"></form></article>',
    githubChromeOcticon: '<article><a class="anchor" aria-label="Permalink: Heading"><svg data-component="Octicon" aria-hidden="true" class="octicon octicon-link"></svg></a></article>',
    githubCopyOcticon: '<article><clipboard-copy class="js-clipboard-copy"><svg data-component="Octicon" aria-hidden="true" class="octicon octicon-copy js-clipboard-copy-icon"></svg></clipboard-copy></article>',
    githubWarningAlertOcticon: '<article><div class="markdown-alert markdown-alert-warning"><p class="markdown-alert-title"><svg data-component="Octicon" aria-hidden="true" class="octicon octicon-alert"></svg>Warning</p></div></article>',
    warningAlertOcticonOutsideTitle: '<article><div class="markdown-alert markdown-alert-warning"><svg data-component="Octicon" aria-hidden="true" class="octicon octicon-alert"></svg><p class="markdown-alert-title">Warning</p></div></article>',
    warningAlertOcticonNestedInTitle: '<article><div class="markdown-alert markdown-alert-warning"><p class="markdown-alert-title"><span><svg data-component="Octicon" aria-hidden="true" class="octicon octicon-alert"></svg></span>Warning</p></div></article>',
    alertTitleOcticonWrongKind: '<article><div class="markdown-alert markdown-alert-danger"><p class="markdown-alert-title"><svg data-component="Octicon" aria-hidden="true" class="octicon octicon-alert"></svg>Warning</p></div></article>',
    authoredWarningAlertOcticon: '<article><div class="markdown-alert-warning"><p class="markdown-alert-title"><svg data-component="Octicon" aria-hidden="true" class="octicon octicon-alert"></svg>Warning</p></div></article>',
    fakeOcticonContent: '<article><p><svg data-component="Octicon" aria-hidden="true" class="octicon octicon-link"></svg></p></article>',
    authoredSvg: '<article><svg aria-hidden="true"><path></path></svg></article>',
  };
  const unsupportedMarkupResult = await cdp.send("Runtime.evaluate", {
    expression: `(() => {
      const check = (${hasVisibleUnsupportedSemanticMarkup.toString()});
      const examples = ${JSON.stringify(unsupportedMarkupExamples)};
      return {
        defaultResults: Object.fromEntries(Object.entries(examples).map(([name, html]) =>
          [name, check(new DOMParser().parseFromString(html, "text/html").body)])),
        chromeResults: {
          githubChromeOcticon: check(new DOMParser().parseFromString(examples.githubChromeOcticon, "text/html").body, true),
          githubCopyOcticon: check(new DOMParser().parseFromString(examples.githubCopyOcticon, "text/html").body, true),
          githubWarningAlertOcticon: check(new DOMParser().parseFromString(examples.githubWarningAlertOcticon, "text/html").body, true),
          warningAlertOcticonOutsideTitle: check(new DOMParser().parseFromString(examples.warningAlertOcticonOutsideTitle, "text/html").body, true),
          warningAlertOcticonNestedInTitle: check(new DOMParser().parseFromString(examples.warningAlertOcticonNestedInTitle, "text/html").body, true),
          alertTitleOcticonWrongKind: check(new DOMParser().parseFromString(examples.alertTitleOcticonWrongKind, "text/html").body, true),
          fakeOcticonContent: check(new DOMParser().parseFromString(examples.fakeOcticonContent, "text/html").body, true),
          authoredSvg: check(new DOMParser().parseFromString(examples.authoredSvg, "text/html").body, true),
        },
        sourceReplayResults: {
          warningAlertOcticon: check(new DOMParser().parseFromString(examples.githubWarningAlertOcticon, "text/html").body),
          authoredWarningAlertOcticon: check(new DOMParser().parseFromString(examples.authoredWarningAlertOcticon, "text/html").body),
        },
      };
    })()`,
    returnByValue: true,
  });
  assert.equal(unsupportedMarkupResult.exceptionDetails, undefined,
    unsupportedMarkupResult.exceptionDetails?.exception?.description || unsupportedMarkupResult.exceptionDetails?.text);
  assert.deepEqual(unsupportedMarkupResult.result?.value, {
    defaultResults: {
      githubStrippedForm: false,
      githubRetainedVideo: true,
      hiddenVideo: false,
      collapsedVideo: false,
      visibleSourceForm: true,
      githubChromeOcticon: true,
      githubCopyOcticon: true,
      githubWarningAlertOcticon: true,
      warningAlertOcticonOutsideTitle: true,
      warningAlertOcticonNestedInTitle: true,
      alertTitleOcticonWrongKind: true,
      authoredWarningAlertOcticon: true,
      fakeOcticonContent: true,
      authoredSvg: true,
    },
    chromeResults: {
      githubChromeOcticon: false,
      githubCopyOcticon: false,
      githubWarningAlertOcticon: false,
      warningAlertOcticonOutsideTitle: true,
      warningAlertOcticonNestedInTitle: true,
      alertTitleOcticonWrongKind: true,
      fakeOcticonContent: true,
      authoredSvg: true,
    },
    sourceReplayResults: {
      warningAlertOcticon: true,
      authoredWarningAlertOcticon: true,
    },
  });

  const replay = await replaySameByteInEdge({
    cdp,
    replayServer,
    outputDirectory,
    viewport: { width: 640, height: 480 },
    maximumTiles: 8,
  });
  assert.equal(replay.status, "passed");
  assert.equal(replay.assets.expectedVisibleImageCount, 7);
  assert.equal(replay.assets.firstViewportRealizedImageCount, 1);
  assert.equal(replay.assets.distinctExpectedUrlHashes, 4);
  assert.equal(replay.assets.distinctServedUrlHashes, 4);
  assert.equal(replay.assets.replayMissCount, 0);
  assert.equal(replay.assets.blockedExternalRequestCount, 0);
  assert.equal(replay.timing.viewportStepRatio, SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO);
  assert.ok(replay.timing.traversalViewportCount > 2,
    "captured-HTML replay should exercise more than two overlapping viewports");
  assert.ok(replay.timing.traversalViewportCount <= MAX_SAME_BYTE_TRAVERSAL_VIEWPORTS);
  assert.ok(replay.timing.firstViewportPaintMs > 0);
  assert.ok(replay.timing.firstViewportImagesReadyMs >= replay.timing.firstViewportPaintMs);
  assert.ok(replay.timing.fullTraversalMs >= replay.timing.firstViewportImagesReadyMs);
  assert.equal(replay.tiles.length, 1);
  assert.equal(existsSync(path.join(outputDirectory, replay.tiles[0].file)), true);

  const sourceReplay = await replaySourceBoundMarkdownInEdge({
    cdp,
    replayServer: sourceReplayServer,
    outputDirectory,
    viewport: { width: 640, height: 480 },
    colorScheme: "light",
    semanticDigestKey: "ab".repeat(32),
    maximumTiles: 8,
  });
  assert.equal(sourceReplay.status, "passed");
  assert.equal(sourceReplay.schemaVersion, 3);
  assert.deepEqual(sourceReplay.styleProfile, {
    id: PINNED_GITHUB_README_STYLE_PROFILE.id,
    version: PINNED_GITHUB_README_STYLE_PROFILE.version,
    sha256: PINNED_GITHUB_README_STYLE_PROFILE.sha256,
    colorScheme: PINNED_GITHUB_README_STYLE_PROFILE.colorScheme,
    edgeStylesheetLoaded: true,
    edgeIntegrityAttributeMatched: true,
    loopbackResponseCount: 1,
  }, "the same-byte oracle must record which exact shared profile Edge loaded and integrity-checked");
  const emojiTextResult = await cdp.send("Runtime.evaluate", {
    expression: "document.querySelector('#readme')?.textContent || ''",
    returnByValue: true,
  });
  assert.equal(emojiTextResult.exceptionDetails, undefined);
  assert.ok(emojiTextResult.result?.value.includes(emojiFixtureValue),
    "source replay must render pinned GitHub emoji shortcodes into their Unicode values");
  assert.ok(!emojiTextResult.result?.value.includes(`:${emojiFixtureAlias}:`),
    "source replay must not leave a supported GitHub emoji shortcode literal");
  assert.equal(sourceReplay.semantic.complete, true);
  assert.equal(sourceReplay.semantic.distinctLinkCount, 1,
    "a linked image with non-empty alt text contributes its same-document fragment destination");
  assert.equal(sourceReplay.semantic.visibleTextTokenCount > 0, true);
  assert.equal(Object.values(sourceReplay.semantic.visibleTextTokenDigests)
    .reduce((sum, count) => sum + count, 0), sourceReplay.semantic.visibleTextTokenCount);
  assert.equal(JSON.stringify(sourceReplay).includes("offline"), false);
  assert.equal(JSON.stringify(sourceReplay).includes("ab".repeat(32)), false);
  assert.equal(sourceReplay.source.readmeGitBlobSha1, gitBlobSha1(readmeBytes));
  assert.equal(sourceReplay.source.readmeSha256, readmeSha256);
  assert.equal(sourceReplay.source.decodedTextSha256, sha256(Buffer.from(readmeBytes.toString("utf8"), "utf8")),
    "the Edge source identity must include the exact decoded text assigned to native Markdown");
  await assert.rejects(replaySourceBoundMarkdownInEdge({
    cdp,
    replayServer: sourceReplayServer,
    outputDirectory,
    viewport: { width: 640, height: 480 },
    colorScheme: "dark",
    semanticDigestKey: "ab".repeat(32),
    maximumTiles: 8,
  }), /pinned Light style profile/u,
  "dark app-theme QA must not silently reuse the Light-only fidelity oracle");
  const retainedHints = await cdp.send("Runtime.evaluate", {
    expression: `(() => ({
      percentage: document.querySelector('#readme img[alt="hidden"]')?.getAttribute('width'),
      legacyPixel: document.querySelector('#readme img[alt="hidden-legacy"]')?.getAttribute('width'),
      collapsedImageCount: document.querySelectorAll('#readme details:not([open]) img').length,
      collapsedImageSources: [...document.querySelectorAll('#readme details:not([open]) img')]
        .map(image => image.getAttribute('src')),
    }))()`,
    returnByValue: true,
  });
  assert.equal(retainedHints.exceptionDetails, undefined);
  assert.deepEqual(retainedHints.result?.value, {
    percentage: "80%",
    legacyPixel: "60px;",
    collapsedImageCount: 2,
    collapsedImageSources: [null, null],
  }, "collapsed details images remain in the DOM but are excluded from initial route/decode completeness");
  const authoredDimensionsResult = await cdp.send("Runtime.evaluate", {
    expression: `(() => {
      const rect = alt => {
        const image = [...document.querySelectorAll("#readme img")].find(item => item.getAttribute("alt") === alt);
        if (!image) return null;
        const bounds = image.getBoundingClientRect();
        return { width: bounds.width, height: bounds.height };
      };
      return { authoredHeight: rect("picture"), authoredRatio: rect("wide") };
    })()`,
    returnByValue: true,
  });
  assert.equal(authoredDimensionsResult.exceptionDetails, undefined);
  assert.deepEqual(authoredDimensionsResult.result?.value, {
    authoredHeight: { width: 100, height: 100 },
    authoredRatio: { width: 640, height: 320 },
  }, "authored height remains honored and width/height scales proportionally under max-width containment");
  assert.deepEqual(sourceReplay.parser, PINNED_SOURCE_REPLAY_PARSER);
  assert.deepEqual(sourceReplay.viewport, {
    width: 640,
    height: 480,
    deviceScaleFactor: 1,
    colorScheme: "light",
    edgeInnerWidth: 640,
    edgeInnerHeight: 480,
    edgeDeviceScaleFactor: 1,
    documentClientWidth: 640,
    bodyClientWidth: 640,
    articleWidth: 640,
  });
  assert.equal(sourceReplay.assets.expectedImageCount, 7,
    "collapsed details images are excluded from the admitted visible-image set");
  assert.equal(sourceReplay.assets.verifiedImageCount, 7,
    "captured network, data, selected picture-source, picture-fallback, and dimensioned images must decode from the pinned Markdown source");
  const pictureGeometry = await cdp.send("Runtime.evaluate", {
    expression: `(() => { const read = alt => { const image = document.querySelector('#readme img[alt="' + alt + '"]'); const rect = image.getBoundingClientRect(); return { width: rect.width, height: rect.height, authoredWidth: image.getAttribute('width'), authoredHeight: image.getAttribute('height') }; }; const selected = document.querySelector('#readme img[alt="source-selected"]'); const ambiguous = document.querySelector('#readme img[alt="ambiguous-fallback"]'); return { pictureElementCount: document.querySelectorAll('#readme picture').length, sourceElementCount: document.querySelectorAll('#readme source').length, pictureFallbackImageCount: document.querySelectorAll('#readme img[alt="picture"]').length, picture: read('picture'), wide: read('wide'), selectedRoute: new URL(selected.src).pathname, ambiguousRoute: new URL(ambiguous.src).pathname }; })()`,
    returnByValue: true,
  });
  assert.deepEqual(pictureGeometry.result?.value, {
    pictureElementCount: 0,
    sourceElementCount: 0,
    pictureFallbackImageCount: 1,
    picture: { width: 100, height: 100, authoredWidth: null, authoredHeight: "100px" },
    wide: { width: 640, height: 320, authoredWidth: "1000px", authoredHeight: "500px" },
    selectedRoute: `/asset-by-url-sha256/${pictureCandidateUrlSha256}`,
    ambiguousRoute: `/asset-by-url-sha256/${pictureUrlSha256}`,
  }, "source replay must preserve pixel-valued HTML hints and their aspect ratio under max-width containment");
  assert.equal(sourceReplay.assets.firstViewportRealizedImageCount, 1,
    "the source-bound Edge replay should start only the top/overscan image before traversal");
  assert.equal(sourceReplay.assets.distinctExpectedUrlHashes, 4);
  assert.equal(sourceReplay.assets.distinctServedUrlHashes, 4);
  assert.equal(sourceReplay.assets.semanticImageAliasMatchCount, 1,
    "unique indexed captured alt identity should recover a transformed URL alias");
  assert.equal(sourceReplay.assets.replayMissCount, 0);
  assert.equal(sourceReplay.assets.blockedExternalRequestCount, 0);
  assert.equal(sourceReplay.timing.viewportStepRatio, SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO);
  assert.ok(sourceReplay.timing.traversalViewportCount > 2,
    "source-bound replay should traverse the complete multi-viewport README");
  assert.ok(sourceReplay.timing.traversalViewportCount <= MAX_SAME_BYTE_TRAVERSAL_VIEWPORTS);
  assert.ok(sourceReplay.timing.firstViewportPaintMs > 0);
  assert.ok(sourceReplay.timing.firstViewportImagesReadyMs >= sourceReplay.timing.firstViewportPaintMs);
  assert.equal(sourceReplay.timing.firstViewportImagesReadyBoundary,
    "same source-available start through successful decode of every visible pinned image");
  assert.ok(sourceReplay.timing.fullTraversalMs >= sourceReplay.timing.firstViewportImagesReadyMs);
  assert.ok(sourceReplay.timing.chargedTraversalMs >= sourceReplay.timing.firstViewportImagesReadyMs);
  assert.ok(sourceReplay.timing.fullTraversalMs > sourceReplay.timing.chargedTraversalMs);
  assert.ok(sourceReplay.timing.auditOnlyFrameWaitMs > 0);
  assert.ok(sourceReplay.timing.auditOnlyFrameWaitCount >= 2);
  assert.ok(Math.abs(sourceReplay.timing.fullTraversalMs -
    sourceReplay.timing.chargedTraversalMs - sourceReplay.timing.auditOnlyFrameWaitMs) < 0.001);
  assert.deepEqual(sourceReplay.renderedExtent, {
    width: 640,
    height: Math.max(480, sourceReplay.tiles.at(-1).relativeY + sourceReplay.tiles.at(-1).height),
  });
  assert.equal(sourceReplay.tiles[0].relativeY, 0);
  assert.equal(sourceReplay.tiles.length, 1);
  assert.equal(existsSync(path.join(outputDirectory, sourceReplay.tiles[0].file)), true);

  const maximumTopViewportUnits = Math.max(0, (sourceReplay.renderedExtent.height - 480) / 480);
  const nativeViewportProfile = {
    schemaVersion: 1,
    viewportHeight: 480,
    viewports: [{ captureOffsetViewportUnits: 0, movementOffsetsViewportUnits: [] }],
  };
  let previousCaptureOffset = 0;
  while (previousCaptureOffset < maximumTopViewportUnits) {
    const captureOffset = Math.min(maximumTopViewportUnits, previousCaptureOffset + 0.8);
    const movementOffsets = [];
    if (nativeViewportProfile.viewports.length === 1 && previousCaptureOffset + 0.95 < maximumTopViewportUnits) {
      movementOffsets.push(previousCaptureOffset + 0.95);
    }
    movementOffsets.push(captureOffset);
    nativeViewportProfile.viewports.push({
      captureOffsetViewportUnits: captureOffset,
      movementOffsetsViewportUnits: movementOffsets,
    });
    previousCaptureOffset = captureOffset;
    assert.ok(nativeViewportProfile.viewports.length <= MAX_SAME_BYTE_TRAVERSAL_VIEWPORTS);
  }
  profileReplayServer = await createSameByteReplayServer(corpusDirectory);
  Object.assign(nativeViewportProfile, {
    readmeGitBlobSha1: profileReplayServer.readmeGitBlobSha1,
    readmeSha256: profileReplayServer.readmeSha256,
    readmeByteSize: profileReplayServer.readmeBytes.length,
    decodedTextSha256: sourceReplay.source.decodedTextSha256,
    nativeEstimatedContentHeightPx: sourceReplay.renderedExtent.height,
  });
  const expectedProfileSource = {
    readmeGitBlobSha1: profileReplayServer.readmeGitBlobSha1,
    readmeSha256: profileReplayServer.readmeSha256,
    byteSize: profileReplayServer.readmeBytes.length,
  };
  assert.equal(validateNativeViewportProfile(nativeViewportProfile, 480, expectedProfileSource), true);
  nativeViewportProfile.sha256 = sha256(Buffer.from(`${JSON.stringify(nativeViewportProfile, null, 2)}\n`));
  const profiledSourceReplay = await replaySourceBoundMarkdownInEdge({
    cdp,
    replayServer: profileReplayServer,
    outputDirectory,
    viewport: { width: 640, height: 480 },
    colorScheme: "light",
    semanticDigestKey: "cd".repeat(32),
    maximumTiles: 8,
    nativeViewportProfile,
  });
  assert.equal(profiledSourceReplay.status, "passed");
  assert.equal(profiledSourceReplay.timing.viewportProfileSha256, nativeViewportProfile.sha256);
  assert.equal(profiledSourceReplay.timing.traversalViewportCount, nativeViewportProfile.viewports.length);
  assert.equal(profiledSourceReplay.timing.movementCount,
    nativeViewportProfile.viewports.reduce((sum, viewport) => sum + viewport.movementOffsetsViewportUnits.length, 0));
  assert.ok(profiledSourceReplay.timing.chargedTraversalMs >=
    profiledSourceReplay.timing.firstViewportImagesReadyMs);
  assert.ok(profiledSourceReplay.timing.fullTraversalMs >= profiledSourceReplay.timing.chargedTraversalMs);
  assert.ok(profiledSourceReplay.timing.auditOnlyFrameWaitCount >= 2);
  assert.ok(Math.abs(profiledSourceReplay.timing.fullTraversalMs -
    profiledSourceReplay.timing.chargedTraversalMs - profiledSourceReplay.timing.auditOnlyFrameWaitMs) < 0.001);
  assert.equal(profiledSourceReplay.timing.captureOffsetsViewportUnits.length, nativeViewportProfile.viewports.length);
  for (let index = 0; index < nativeViewportProfile.viewports.length; index++) {
    assert.ok(Math.abs(profiledSourceReplay.timing.captureOffsetsViewportUnits[index] -
      nativeViewportProfile.viewports[index].captureOffsetViewportUnits) <= 1 / 480 + 0.000001);
  }

  const terminalClampProfile = {
    schemaVersion: 1,
    viewportHeight: nativeViewportProfile.viewportHeight,
    readmeGitBlobSha1: nativeViewportProfile.readmeGitBlobSha1,
    readmeSha256: nativeViewportProfile.readmeSha256,
    readmeByteSize: nativeViewportProfile.readmeByteSize,
    decodedTextSha256: nativeViewportProfile.decodedTextSha256,
    nativeEstimatedContentHeightPx: sourceReplay.renderedExtent.height + 48,
    viewports: nativeViewportProfile.viewports.map(viewport => ({
      captureOffsetViewportUnits: viewport.captureOffsetViewportUnits,
      movementOffsetsViewportUnits: [...viewport.movementOffsetsViewportUnits],
    })),
  };
  const terminalNativeCaptureOffsets = [maximumTopViewportUnits + 0.05, maximumTopViewportUnits + 0.1];
  for (const captureOffsetViewportUnits of terminalNativeCaptureOffsets) {
    terminalClampProfile.viewports.push({
      captureOffsetViewportUnits,
      movementOffsetsViewportUnits: [captureOffsetViewportUnits],
    });
  }
  assert.equal(validateNativeViewportProfile(terminalClampProfile, 480, expectedProfileSource), true,
    "the terminal suffix remains monotonic and within the native overlap budget");
  assert.equal(isSourceReplayExtentCompatible(
    terminalClampProfile.nativeEstimatedContentHeightPx,
    sourceReplay.renderedExtent.height,
    480), true, "the terminal profile remains within the full-page extent envelope");
  terminalClampProfile.sha256 = sha256(Buffer.from(`${JSON.stringify(terminalClampProfile, null, 2)}\n`));
  const terminalClampOutputDirectory = path.join(outputDirectory, "terminal-clamp");
  await mkdir(terminalClampOutputDirectory);
  const terminalClampReplay = await replaySourceBoundMarkdownInEdge({
    cdp,
    replayServer: profileReplayServer,
    outputDirectory: terminalClampOutputDirectory,
    viewport: { width: 640, height: 480 },
    colorScheme: "light",
    semanticDigestKey: "ef".repeat(32),
    maximumTiles: 8,
    nativeViewportProfile: terminalClampProfile,
  });
  const terminalEdgeMaximumTop = Math.max(0, (terminalClampReplay.renderedExtent.height - 480) / 480);
  assert.equal(terminalClampReplay.status, "passed");
  assert.equal(terminalClampReplay.timing.viewportProfileSha256, terminalClampProfile.sha256,
    "the native profile hash is preserved and never rewritten to accommodate Edge");
  const terminalCaptureOffsets = terminalClampReplay.timing.captureOffsetsViewportUnits.slice(-3);
  assert.equal(terminalCaptureOffsets[1], terminalEdgeMaximumTop);
  assert.equal(terminalCaptureOffsets[2], terminalEdgeMaximumTop,
    "two distinct terminal native captures may map to the same verified Edge bottom");
  assert.ok(terminalEdgeMaximumTop - terminalCaptureOffsets[0] >= -1 / 480 - 0.000001 &&
    terminalEdgeMaximumTop - terminalCaptureOffsets[0] <= 0.9 + 1 / 480 + 0.000001,
  "the final transition into the clamped suffix retains bounded coverage");
  assert.equal(terminalClampReplay.timing.captureOffsetsViewportUnits.at(-2),
    terminalClampReplay.timing.captureOffsetsViewportUnits.at(-1));
  assert.deepEqual(terminalClampReplay.timing.terminalClampObservations.map(observation => ({
    viewportIndex: observation.viewportIndex,
    movementIndex: observation.movementIndex,
    requestedOffsetViewportUnits: observation.requestedOffsetViewportUnits,
    actualOffsetViewportUnits: observation.actualOffsetViewportUnits,
    maximumOffsetViewportUnits: observation.maximumOffsetViewportUnits,
    documentHeightPx: observation.documentHeightPx,
    viewportHeightPx: observation.viewportHeightPx,
  })), terminalNativeCaptureOffsets.flatMap((requestedOffsetViewportUnits, index) => [
    {
      viewportIndex: terminalClampProfile.viewports.length - terminalNativeCaptureOffsets.length + index,
      movementIndex: 0,
      requestedOffsetViewportUnits,
      actualOffsetViewportUnits: terminalEdgeMaximumTop,
      maximumOffsetViewportUnits: terminalEdgeMaximumTop,
      documentHeightPx: terminalClampReplay.renderedExtent.height,
      viewportHeightPx: 480,
    },
    {
      viewportIndex: terminalClampProfile.viewports.length - terminalNativeCaptureOffsets.length + index,
      movementIndex: -1,
      requestedOffsetViewportUnits,
      actualOffsetViewportUnits: terminalEdgeMaximumTop,
      maximumOffsetViewportUnits: terminalEdgeMaximumTop,
      documentHeightPx: terminalClampReplay.renderedExtent.height,
      viewportHeightPx: 480,
    },
  ]), "every collapsed movement and capture has ordered, final-bottom evidence");
  assert.equal(terminalClampReplay.timing.sourceTailViewportCount, 0,
    "source-tail traversal starts from the repeated actual Edge bottom instead of inventing a lower offset");

  const failedDecodeCorpusDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-decode-failure-"));
  t.after(async () => { await rm(failedDecodeCorpusDirectory, { recursive: true, force: true }); });
  const failedImageBytes = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const failedImageUrl = "https://raw.githubusercontent.com/example/repo/0123456789012345678901234567890123456789/broken.png";
  const failedReadmeBytes = Buffer.from("![broken](" + failedImageUrl + ")\n", "utf8");
  const failedRenderedHtmlBytes = Buffer.from(
    '<article class="markdown-body"><img alt="broken" data-jithub-image-index="0"></article>',
    "utf8");
  const failedImageSha256 = sha256(failedImageBytes);
  const failedUrlSha256 = resourceUrlSha256(failedImageUrl);
  await mkdir(path.join(failedDecodeCorpusDirectory, "assets"));
  await writeFile(path.join(failedDecodeCorpusDirectory, "readme.md"), failedReadmeBytes);
  await writeFile(path.join(failedDecodeCorpusDirectory, "rendered.html"), failedRenderedHtmlBytes);
  await writeFile(path.join(failedDecodeCorpusDirectory, "assets", failedImageSha256), failedImageBytes);
  const failedDecodeManifest = {
    schemaVersion: 2,
    complete: true,
    repository: {
      fullName: "example/repo",
      commitSha: "0123456789012345678901234567890123456789",
      readmePath: "README.md",
      readmeGitBlobSha1: gitBlobSha1(failedReadmeBytes),
    },
    readme: { file: "readme.md", byteSize: failedReadmeBytes.length, sha256: sha256(failedReadmeBytes) },
    assets: [{
      urlSha256: failedUrlSha256,
      sha256: failedImageSha256,
      byteSize: failedImageBytes.length,
      mimeType: "image/png",
    }],
    imageRoutes: [{
      index: 0,
      urlSha256: failedUrlSha256,
      sha256: failedImageSha256,
      mimeType: "image/png",
    }],
    browserRender: {
      status: "passed",
      file: "rendered.html",
      byteSize: failedRenderedHtmlBytes.length,
      sha256: sha256(failedRenderedHtmlBytes),
    },
    limits: {
      maxReadmeBytes: 16 * 1024 * 1024,
      maxRenderedHtmlBytes: 32 * 1024 * 1024,
      maxAssetBytes: 64 * 1024 * 1024,
      maxAggregateAssetBytes: 256 * 1024 * 1024,
    },
  };
  await writeFile(
    path.join(failedDecodeCorpusDirectory, "manifest.json"),
    JSON.stringify(failedDecodeManifest, null, 2) + "\n");
  const failedDecodeReplayServer = await createSameByteReplayServer(failedDecodeCorpusDirectory);
  t.after(async () => { await failedDecodeReplayServer.close(); });
  let failedDecodeReport;
  await assert.rejects(replaySourceBoundMarkdownInEdge({
    cdp,
    replayServer: failedDecodeReplayServer,
    outputDirectory,
    viewport: { width: 640, height: 480 },
    colorScheme: "light",
    semanticDigestKey: "ef".repeat(32),
    maximumTiles: 8,
  }), error => {
    assert.equal(error.sourceReplayStage, "visible-image-decode");
    failedDecodeReport = createSourceReplayFailureReport(error);
    return true;
  });
  assert.equal(failedDecodeReport.failureCategory, "image");
  const failedImageEvidence = failedDecodeReport.imageDecodeEvidence;
  assert.equal(failedImageEvidence.visibleImageCount, 1);
  assert.equal(failedImageEvidence.failedVisibleImageCount, 1);
  assert.deepEqual(failedImageEvidence.images, [{
    imageIndex: 0,
    capturedUrlSha256: failedUrlSha256,
    dataUri: false,
    ready: false,
    decodeOutcome: failedImageEvidence.images[0].decodeOutcome,
  }]);
  assert.ok(["load-error", "already-complete-empty"].includes(failedImageEvidence.images[0].decodeOutcome),
    "a broken pinned payload may reject before or just after Edge observes its error event");
  assert.equal(failedImageEvidence.omittedVisibleImageCount, 0);

  const routeMutationCorpusDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-route-mutation-"));
  t.after(async () => { await rm(routeMutationCorpusDirectory, { recursive: true, force: true }); });
  await cp(corpusDirectory, routeMutationCorpusDirectory, { recursive: true });
  const routeMutationManifestPath = path.join(routeMutationCorpusDirectory, "manifest.json");
  const routeMutationManifest = JSON.parse(await readFile(routeMutationManifestPath, "utf8"));
  const wrongPayloadRoute = routeMutationManifest.imageRoutes.find(route => route.index === 0);
  routeMutationManifest.imageRoutes = routeMutationManifest.imageRoutes.map(route =>
    route.index === 1 ? { ...wrongPayloadRoute, index: 1 } : route);
  await writeFile(routeMutationManifestPath, JSON.stringify(routeMutationManifest, null, 2) + "\n");
  const routeMutationReplayServer = await createSameByteReplayServer(routeMutationCorpusDirectory);
  t.after(async () => { await routeMutationReplayServer.close(); });
  await assert.rejects(replaySourceBoundMarkdownInEdge({
    cdp,
    replayServer: routeMutationReplayServer,
    outputDirectory,
    viewport: { width: 640, height: 480 },
    colorScheme: "light",
    semanticDigestKey: "ab".repeat(32),
    maximumTiles: 8,
  }), error => {
    assert.equal(error.sourceReplayStage, "image-map");
    assert.equal(error.sourceReplayFailurePredicate, "captured-image-route-mismatch");
    return true;
  }, "same-count route mutation cannot pass with the wrong captured payload identity");

  // Top-500 run 36501559942 ranks 3 and 16 both contain visible HTML images
  // with a browser-valid percentage width hint. Replay from a tiny, fully
  // hash-bound local corpus so this exercises the actual source sanitizer and
  // image decoder without relying on GitHub or external image delivery.
  const visiblePercentageWidthCases = [
    {
      rank: 3,
      fullName: "public-apis/public-apis",
      commitSha: "8939d468fa4580039c4e22cc4998eb3fef8130d1",
      imagePath: ".github/cs1586-APILayerLogoUpdate2022-LJ_v2-HighRes.png",
      alt: "APILayer Logo",
    },
    {
      rank: 16,
      fullName: "affaan-m/ECC",
      commitSha: "d3b8a3e908904e242ed2dbe66af62cca71131419",
      imagePath: "assets/hero.png",
      alt: "ECC - the agent harness operating system",
    },
  ];
  const percentageImageBytes = Buffer.from(
    dataImage.slice(dataImage.indexOf(",") + 1),
    "base64",
  );
  const percentageImageSha256 = sha256(percentageImageBytes);
  for (const fixture of visiblePercentageWidthCases) {
    const percentageCorpusDirectory = await mkdtemp(
      path.join(os.tmpdir(), `jithub-rank${fixture.rank}-percentage-image-width-`));
    const percentageOutputDirectory = await mkdtemp(
      path.join(os.tmpdir(), `jithub-rank${fixture.rank}-percentage-image-output-`));
    t.after(async () => {
      await rm(percentageCorpusDirectory, { recursive: true, force: true });
      await rm(percentageOutputDirectory, { recursive: true, force: true });
    });
    const [owner, repositoryName] = fixture.fullName.split("/");
    const imageUrl = new URL(
      fixture.imagePath,
      `https://github.com/${owner}/${repositoryName}/blob/${fixture.commitSha}/README.md`,
    ).href;
    const imageUrlSha256 = resourceUrlSha256(imageUrl);
    const percentageReadmeBytes = Buffer.from(
      `<img src="${fixture.imagePath}" alt="${fixture.alt}" width="100%" />\n`,
      "utf8",
    );
    const percentageHtmlBytes = Buffer.from(
      `<article class="markdown-body"><img alt="${fixture.alt}" width="100%" data-jithub-image-index="0"></article>`,
      "utf8",
    );
    const percentageManifest = {
      schemaVersion: 2,
      complete: true,
      repository: {
        fullName: fixture.fullName,
        commitSha: fixture.commitSha,
        readmePath: "README.md",
        readmeGitBlobSha1: gitBlobSha1(percentageReadmeBytes),
      },
      readme: {
        file: "readme.md",
        byteSize: percentageReadmeBytes.length,
        sha256: sha256(percentageReadmeBytes),
      },
      assets: [{
        urlSha256: imageUrlSha256,
        sha256: percentageImageSha256,
        byteSize: percentageImageBytes.length,
        mimeType: "image/png",
      }],
      imageRoutes: [{
        index: 0,
        urlSha256: imageUrlSha256,
        sha256: percentageImageSha256,
        mimeType: "image/png",
      }],
      browserRender: {
        status: "passed",
        file: "rendered.html",
        byteSize: percentageHtmlBytes.length,
        sha256: sha256(percentageHtmlBytes),
      },
      limits: {
        maxReadmeBytes: 16 * 1024 * 1024,
        maxRenderedHtmlBytes: 32 * 1024 * 1024,
        maxAssetBytes: 64 * 1024 * 1024,
        maxAggregateAssetBytes: 256 * 1024 * 1024,
      },
    };
    await mkdir(path.join(percentageCorpusDirectory, "assets"));
    await writeFile(path.join(percentageCorpusDirectory, "readme.md"), percentageReadmeBytes);
    await writeFile(path.join(percentageCorpusDirectory, "rendered.html"), percentageHtmlBytes);
    await writeFile(
      path.join(percentageCorpusDirectory, "assets", percentageImageSha256), percentageImageBytes);
    await writeFile(
      path.join(percentageCorpusDirectory, "manifest.json"),
      `${JSON.stringify(percentageManifest, null, 2)}\n`);
    const percentageReplayServer = await createSameByteReplayServer(percentageCorpusDirectory);
    t.after(async () => { await percentageReplayServer.close(); });

    const percentageReplay = await replaySourceBoundMarkdownInEdge({
      cdp,
      replayServer: percentageReplayServer,
      outputDirectory: percentageOutputDirectory,
      viewport: { width: 640, height: 480 },
      colorScheme: "light",
      semanticDigestKey: "9a".repeat(32),
      maximumTiles: 8,
    });
    assert.equal(percentageReplay.status, "passed", `rank ${fixture.rank} width=100% source should replay`);
    assert.equal(percentageReplay.source.readmeGitBlobSha1, gitBlobSha1(percentageReadmeBytes));
    assert.equal(percentageReplay.assets.expectedImageCount, 1);
    assert.equal(percentageReplay.assets.verifiedImageCount, 1);
    assert.equal(percentageReplay.assets.distinctExpectedUrlHashes, 1);
    assert.equal(percentageReplay.assets.distinctServedUrlHashes, 1);
    assert.equal(percentageReplay.assets.replayMissCount, 0);
    assert.equal(percentageReplay.assets.blockedExternalRequestCount, 0,
      "the exact PNG bytes must be served only from the local replay corpus");
    const visibleDimensionResult = await cdp.send("Runtime.evaluate", {
      expression: "(() => { const image = document.querySelector('#readme img'); return [image?.getAttribute('width'), image?.complete, image?.naturalWidth]; })()",
      returnByValue: true,
    });
    assert.equal(visibleDimensionResult.exceptionDetails, undefined);
    assert.deepEqual(visibleDimensionResult.result?.value, ["100%", true, 1],
      `rank ${fixture.rank} must preserve the authored width hint and decode its captured bytes`);
  }

  const invalidDimensionCorpusDirectory = await mkdtemp(path.join(os.tmpdir(), "jithub-same-byte-invalid-image-dimension-"));
  t.after(async () => { await rm(invalidDimensionCorpusDirectory, { recursive: true, force: true }); });
  const invalidDimensionReadmeBytes = Buffer.from(
    `<img src="${failedImageUrl}" alt="bad dimension" width="100000%">\n`, "utf8");
  const invalidDimensionHtmlBytes = Buffer.from(
    '<article class="markdown-body"><img alt="bad dimension" width="1" height="1" data-jithub-image-index="0"></article>',
    "utf8");
  await mkdir(path.join(invalidDimensionCorpusDirectory, "assets"));
  await writeFile(path.join(invalidDimensionCorpusDirectory, "readme.md"), invalidDimensionReadmeBytes);
  await writeFile(path.join(invalidDimensionCorpusDirectory, "rendered.html"), invalidDimensionHtmlBytes);
  await writeFile(path.join(invalidDimensionCorpusDirectory, "assets", failedImageSha256), failedImageBytes);
  const invalidDimensionManifest = {
    ...failedDecodeManifest,
    repository: {
      ...failedDecodeManifest.repository,
      readmeGitBlobSha1: gitBlobSha1(invalidDimensionReadmeBytes),
    },
    readme: {
      file: "readme.md",
      byteSize: invalidDimensionReadmeBytes.length,
      sha256: sha256(invalidDimensionReadmeBytes),
    },
    browserRender: {
      status: "passed",
      file: "rendered.html",
      byteSize: invalidDimensionHtmlBytes.length,
      sha256: sha256(invalidDimensionHtmlBytes),
    },
  };
  await writeFile(path.join(invalidDimensionCorpusDirectory, "manifest.json"),
    JSON.stringify(invalidDimensionManifest, null, 2) + "\n");
  const invalidDimensionReplayServer = await createSameByteReplayServer(invalidDimensionCorpusDirectory);
  t.after(async () => { await invalidDimensionReplayServer.close(); });
  await assert.rejects(replaySourceBoundMarkdownInEdge({
    cdp,
    replayServer: invalidDimensionReplayServer,
    outputDirectory,
    viewport: { width: 640, height: 480 },
    colorScheme: "light",
    semanticDigestKey: "ef".repeat(32),
    maximumTiles: 8,
  }), error => {
    assert.equal(error.sourceReplayStage, "sanitize-markdown");
    assert.match(error.message, /invalid authored width or height/u);
    return true;
  });
});

async function readRenderedSnapshotExpression() {
  const oraclePath = new URL("./browser-oracle.mjs", import.meta.url);
  const source = await readFile(oraclePath, "utf8");
  const functionStart = source.indexOf("async function captureRenderedHtmlSnapshot(cdpClient, excludedDecorativeImageReplayIndexes = []) {");
  assert.notEqual(functionStart, -1, "browser-oracle should expose the snapshot implementation in its source");
  const marker = "const html = await evaluate(cdpClient, `";
  const expressionStart = source.indexOf(marker, functionStart);
  assert.notEqual(expressionStart, -1, "snapshot implementation should evaluate its generated expression");
  const bodyStart = expressionStart + marker.length;
  const bodyEnd = source.indexOf("`);", bodyStart);
  assert.notEqual(bodyEnd, -1, "snapshot expression should have a terminated template literal");
  const templateBody = source.slice(bodyStart, bodyEnd);
  // Recreate JavaScript's own template-literal escape/interpolation semantics
  // exactly as captureRenderedHtmlSnapshot does before sending the string to
  // Edge. This catches cases where source escaping makes Edge see an undefined
  // identifier (for example `iu`) instead of the intended regex delimiter.
  return new Function("excludedDecorativeImageReplayIndexes", `return \`${templateBody}\`;`)([]);
}

function connectCdp(url) {
  const socket = new WebSocket(url);
  return new Promise((resolve, reject) => {
    const pending = new Map();
    const listeners = new Map();
    let sequence = 0;
    socket.addEventListener("open", () => resolve({
      send(method, params = {}) {
        const id = ++sequence;
        return new Promise((sendResolve, sendReject) => {
          pending.set(id, { resolve: sendResolve, reject: sendReject });
          socket.send(JSON.stringify({ id, method, params }));
        });
      },
      on(method, listener) {
        const items = listeners.get(method) || new Set();
        items.add(listener);
        listeners.set(method, items);
        return () => items.delete(listener);
      },
      close() { socket.close(); },
    }), { once: true });
    socket.addEventListener("error", event => reject(event.error || new Error("Edge DevTools socket failed.")), { once: true });
    socket.addEventListener("message", event => {
      const message = JSON.parse(event.data);
      if (message.id) {
        const waiter = pending.get(message.id);
        if (!waiter) return;
        pending.delete(message.id);
        message.error ? waiter.reject(new Error(message.error.message)) : waiter.resolve(message.result || {});
        return;
      }
      for (const listener of listeners.get(message.method) || []) {
        try { listener(message.params || {}); } catch {}
      }
    });
  });
}
