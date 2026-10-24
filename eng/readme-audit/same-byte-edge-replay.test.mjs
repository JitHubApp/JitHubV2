import assert from "node:assert/strict";
import { createHmac, webcrypto } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import {
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
import {
  replayFunctionDeclaration,
  replayImageOmissionGeometryMatches,
} from "./same-byte-edge-replay.mjs";
import { readRenderedSnapshotExpression } from "./snapshot-expression.test-support.mjs";
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
    sourceLessDecorativeImageSpacers: [],
    cssHiddenImageOmissions: [],
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
    sourceLessDecorativeImageSpacers: [],
    cssHiddenImageOmissions: [],
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


test("rendered snapshot expression injects its closed-details helper", async () => {
  const expression = await readRenderedSnapshotExpression();
  const helperDeclaration = expression.indexOf("const pruneClosedDetailsReplayContent = function pruneClosedDetailsReplayContent(root)");
  const helperCall = expression.indexOf("pruneClosedDetailsReplayContent(clone)");
  const spacerPredicateDeclaration = expression.indexOf(
    "const isSourceLessDecorativeImageSpacerCandidate = function isSourceLessDecorativeImageSpacerCandidate(candidate)");
  const svgResourcePredicateDeclaration = expression.indexOf(
    "const hasUncapturedExternalInlineSvgResource = function hasUncapturedExternalInlineSvgResource({");
  const hiddenImageMap = expression.indexOf("const cssHiddenImageOmissionsByIndex = new Map(cssHiddenImageOmissions.map(item => [item.index, item]))");
  const hiddenImageInventoryCheck = expression.indexOf("observedCssHiddenImageOmissionIndexes.size !== cssHiddenImageOmissionsByIndex.size");
  const snapshotReturn = expression.indexOf("html: clone.outerHTML");

  assert.ok(helperDeclaration >= 0 && spacerPredicateDeclaration >= 0 &&
    svgResourcePredicateDeclaration >= 0 && hiddenImageMap >= 0,
    "the browser expression must inline closed-details/spacer/SVG-resource behavior and bind CSS-hidden indices");
  assert.ok(helperDeclaration < helperCall && helperCall < snapshotReturn &&
    spacerPredicateDeclaration < snapshotReturn && svgResourcePredicateDeclaration < snapshotReturn &&
    hiddenImageMap < hiddenImageInventoryCheck &&
    hiddenImageInventoryCheck < snapshotReturn,
  "the captured clone must prove its full CSS-hidden omission inventory before serialization");
  assert.match(expression,
    /throw new Error\("GitHub article changed its CSS-hidden image inventory during same-byte capture\."\)/u,
    "a newly hidden image cannot silently gain the route-less exception");
  assert.match(expression,
    /if \(hasUncapturedExternalInlineSvgResource\(\{\s*namespaceURI: source\.namespaceURI,\s*localName: source\.localName,/u,
    "every SVG href is checked using namespace and local name before generic href sanitization");
});

test("route-free image omissions are checked against post-attachment geometry before visibility exclusion", () => {
  const hidden = {
    cssDisplayNone: true,
    zeroBounds: true,
    renderedWidth: 0,
    renderedHeight: 0,
  };
  assert.equal(replayImageOmissionGeometryMatches("css-hidden", hidden,
    { display: "none" }, { width: 0, height: 0 }), true);
  assert.equal(replayImageOmissionGeometryMatches("css-hidden", hidden,
    { display: "block" }, { width: 0, height: 0 }), false,
  "a route-free hidden marker cannot hide an image whose attached computed display is visible");
  assert.equal(replayImageOmissionGeometryMatches("css-hidden", hidden,
    { display: "none" }, { width: 1, height: 0 }), false);

  const spacer = { width: 16, height: 1, renderedWidth: 16, renderedHeight: 1 };
  assert.equal(replayImageOmissionGeometryMatches("layout-spacer", spacer,
    { width: "16px", height: "1px" }, { width: 16, height: 1 }), true);
  assert.equal(replayImageOmissionGeometryMatches("layout-spacer", spacer,
    { width: "900px", height: "1px" }, { width: 900, height: 1 }), false,
  "spacer exclusion requires the captured CSS and rendered dimensions");
  assert.equal(replayImageOmissionGeometryMatches("layout-spacer", spacer,
    { width: "16px", height: "1px" }, { width: 16, height: 2 }), false);

  const replay = replayFunctionDeclaration();
  const attachment = replay.indexOf("document.body.replaceChildren(article)");
  const geometryCheck = replay.indexOf("replayImageOmissionGeometryMatches(\"css-hidden\"");
  const visibleCheck = replay.indexOf("const visibleImages = () =>");
  assert.ok(attachment >= 0 && attachment < geometryCheck && geometryCheck < visibleCheck,
    "actual computed CSS and bounds are checked after attachment and before omitted images leave readiness checks");
});


test("terminal readiness decodes only bound routes when route zero coexists with omissions", async () => {
  const createImage = (attributes, width, height, decoded) => ({
    attributes: new Map(Object.entries(attributes)),
    style: { display: attributes["data-jithub-hidden-image-index"] ? "none" : "inline" },
    complete: true,
    naturalWidth: decoded ? width : 0,
    naturalHeight: decoded ? height : 0,
    decodeCount: 0,
    getAttribute(name) { return this.attributes.get(name) ?? null; },
    hasAttribute(name) { return this.attributes.has(name); },
    removeAttribute(name) { this.attributes.delete(name); },
    addEventListener() {},
    removeEventListener() {},
    getBoundingClientRect() {
      return { top: 0, left: 0, bottom: height, right: width, width, height };
    },
    async decode() { this.decodeCount++; },
  });
  const zero = createImage({ "data-jithub-image-index": "0" }, 24, 24, true);
  const hidden = createImage({ "data-jithub-hidden-image-index": "1" }, 0, 0, false);
  const spacer = createImage({ "data-jithub-layout-spacer-index": "2", alt: "" }, 16, 1, false);
  const images = [zero, hidden, spacer];
  const article = {
    tagName: "ARTICLE",
    getAttribute() { return null; },
    querySelectorAll(selector) { assert.equal(selector, "img"); return images; },
  };
  const document = {
    images,
    documentElement: { scrollWidth: 64, scrollHeight: 64 },
    body: { style: {}, replaceChildren(value) { assert.equal(value, article); } },
  };
  const run = new Function("DOMParser", "document", "getComputedStyle", "requestAnimationFrame",
    "scrollY", "innerHeight", "innerWidth", "scrollTo", "addEventListener", "removeEventListener",
    `return (${replayFunctionDeclaration()});`)(
    class {
      parseFromString() {
        return { body: { firstElementChild: article }, querySelector() { return null; }, querySelectorAll() { return []; } };
      }
    }, document,
    image => ({ display: image.style.display, width: "16px", height: "1px" }),
    callback => callback(), 0, 64, 64, () => {}, () => {}, () => {});
  const invoke = () => run("<article></article>", [{ index: 0, sha256: "a".repeat(64) }],
    [{ index: 2, width: 16, height: 1, renderedWidth: 16, renderedHeight: 1 }],
    [{ index: 1, cssDisplayNone: true, zeroBounds: true, renderedWidth: 0, renderedHeight: 0 }],
    "http://127.0.0.1:43210", 100, 2, .8);

  const result = await invoke();
  assert.equal(result.loadedImageCount, 1);
  assert.equal(result.firstViewportRealizedImageCount, 1);
  assert.equal(result.capturedImageElementCount, 3);
  assert.ok(zero.decodeCount > 0);
  assert.equal(hidden.decodeCount, 0);
  assert.equal(spacer.decodeCount, 0);
  assert.match(zero.src, /\/asset-by-content-sha256\/a{64}\?index=0$/u);
  assert.equal(hidden.src, undefined);
  assert.equal(spacer.src, undefined);

  for (const invalid of [null, "", " ", "NaN", "0.0"]) {
    if (invalid === null) zero.attributes.delete("data-jithub-image-index");
    else zero.attributes.set("data-jithub-image-index", invalid);
    await assert.rejects(invoke(), /rendered image without a captured route/u,
      "an absent or malformed real route index must never alias route zero");
  }
});
