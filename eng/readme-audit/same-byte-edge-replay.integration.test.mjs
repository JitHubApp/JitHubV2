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
  isSourceLessDecorativeImageSpacerCandidate,
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
import { readRenderedSnapshotExpression } from "./snapshot-expression.test-support.mjs";
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
  // The source-less spacer case is pinned from browser-use/browser-use at
  // commit d8110c5ff87ccba887aaa726cdb780f2f84bef8d, README blob
  // ae0462a41e3479defd70fadc587d55751e7d7ada (authored line 24:
  // <img width="16" height="1" alt="">). The fixture below also tries to
  // spoof harness metadata; capture must strip it and bind a fresh ordinal.
  const html = `<article class="markdown-body" style="width:320px;color:#222;font:14px Arial"><p>offline replay</p><img alt="offline" width="2" height="2" data-jithub-image-index="0">${traversalHtml}<img alt="picture" width="100" height="100" data-jithub-image-index="1"><img alt="wide" width="1000" height="500" data-jithub-image-index="2"><img alt="deferred" width="2" height="2" data-jithub-image-index="3"><img alt="inline" data-jithub-image-index="4" data-jithub-image-data="${dataImage}"><img alt="source-selected" width="32" height="24" data-jithub-image-index="5"><img alt="ambiguous-fallback" data-jithub-image-index="6"><img alt="" style="width:16px;height:1px" data-jithub-layout-spacer-index="7"><img alt="theme-hidden" style="display:none;width:0px;height:0px" data-jithub-hidden-image-index="8"></article>`;
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
    sourceLessDecorativeImageSpacers: [
      { index: 7, width: 16, height: 1, renderedWidth: 16, renderedHeight: 1 },
    ],
    cssHiddenImageOmissions: [
      { index: 8, cssDisplayNone: true, zeroBounds: true, renderedWidth: 0, renderedHeight: 0 },
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
    expression: `document.body.innerHTML = ${JSON.stringify(`<div id="readme"><article class="markdown-body"><p>snapshot syntax</p><img alt="inline" src="${dataImage}"><details><summary>Collapsed summary</summary><p>closed-only content</p><img alt="closed-detail-image" src="${dataImage}"></details><details open><summary>Expanded summary</summary><p>open-detail content</p><img alt="open-detail-image" src="${dataImage}"></details><img alt="" width="16" height="1" style="width:16px;height:1px" data-jithub-image-index="999" data-jithub-layout-spacer-index="999" data-jithub-image-data="spoofed"><img alt="hidden" src="${dataImage}" style="display:none;width:0;height:0" data-jithub-hidden-image-index="999"></article></div>`)}`,
    returnByValue: true,
  });
  const snapshotSpacer = { index: 3, width: 16, height: 1, renderedWidth: 16, renderedHeight: 1 };
  const snapshotHidden = { index: 4, cssDisplayNone: true, zeroBounds: true, renderedWidth: 0, renderedHeight: 0 };
  assert.equal(isSourceLessDecorativeImageSpacerCandidate({
    hasExplicitAlt: true,
    alt: "",
    hasSrcAttribute: false,
    source: "",
    currentSource: "",
    hasSrcsetAttribute: false,
    srcset: "",
    hasCanonicalSourceAttribute: false,
    canonicalSource: "",
    hasDataSourceAttribute: false,
    dataSource: "",
    hasDataSrcsetAttribute: false,
    dataSrcset: "",
    hasDataLazySourceAttribute: false,
    dataLazySource: "",
    pictureHasSourceResource: false,
    width: 16,
    height: 1,
    renderedWidth: 16,
    renderedHeight: 1,
  }), true);
  const snapshotExpression = await readRenderedSnapshotExpression([snapshotSpacer], [snapshotHidden]);
  const snapshotResult = await cdp.send("Runtime.evaluate", {
    expression: snapshotExpression,
    returnByValue: true,
  });
  assert.equal(snapshotResult.exceptionDetails, undefined,
    snapshotResult.exceptionDetails?.exception?.description || snapshotResult.exceptionDetails?.text);
  const capturedSnapshot = snapshotResult.result?.value;
  const capturedHtml = capturedSnapshot?.html;
  assert.deepEqual(capturedSnapshot?.sourceLessDecorativeImageSpacers, [snapshotSpacer]);
  assert.deepEqual(capturedSnapshot?.cssHiddenImageOmissions, [snapshotHidden]);
  assert.match(capturedHtml, /data-jithub-image-data="data:image\/png;base64,/iu);
  assert.doesNotMatch(capturedHtml, /\ssrc=/iu);
  assert.match(capturedHtml, /Collapsed summary/u);
  assert.doesNotMatch(capturedHtml, /closed-only content|closed-detail-image/u,
    "closed-details descendants are absent from the static rendered snapshot");
  assert.match(capturedHtml, /Expanded summary/u);
  assert.match(capturedHtml, /open-detail content/u);
  assert.match(capturedHtml, /alt="open-detail-image"/u,
    "open-details content and its inline image remain in the rendered snapshot");
  assert.match(capturedHtml, /data-jithub-layout-spacer-index="3"/u,
    "the authorized spacer is rebound to its ordinal from the original article");
  assert.match(capturedHtml, /data-jithub-hidden-image-index="4"/u,
    "the hidden resource-backed image is rebound to its original ordinal");
  assert.match(capturedHtml, /style="[^"]*display: none/u,
    "the hidden resource-backed image keeps an explicitly hidden, route-free box");
  assert.doesNotMatch(capturedHtml, /data-jithub-layout-spacer-index="999"|data-jithub-image-index="999"|data-jithub-image-data="spoofed"/u,
    "page-authored internal metadata is discarded before harness markers are applied");

  const sourceDetailsResult = await cdp.send("Runtime.evaluate", {
    expression: `(() => ({
      closedContent: document.querySelector("#readme details:not([open])")?.textContent,
      closedImageCount: document.querySelectorAll("#readme details:not([open]) img").length,
      openContent: document.querySelector("#readme details[open]")?.textContent,
      imageCount: document.querySelectorAll("#readme img").length,
    }))()`,
    returnByValue: true,
  });
  assert.equal(sourceDetailsResult.exceptionDetails, undefined);
  assert.deepEqual(sourceDetailsResult.result?.value, {
    closedContent: "Collapsed summaryclosed-only content",
    closedImageCount: 1,
    openContent: "Expanded summaryopen-detail content",
    imageCount: 5,
  }, "capture prunes only its clone and leaves the source document unchanged");

  // Static inline declarations alone cannot prove the eventual layout. A
  // stylesheet can override them after the detached article is attached.
  const omissionWindow = await cdp.send("Runtime.evaluate", {
    expression: "window",
    returnByValue: false,
  });
  assert.ok(omissionWindow.result?.objectId);
  for (const fixture of [
    {
      html: '<article><style>img { display:block !important }</style><img style="display:none;width:0px;height:0px" data-jithub-hidden-image-index="4"></article>',
      spacers: [],
      hidden: [snapshotHidden],
      error: /changed CSS-hidden image visibility or geometry after attachment/u,
    },
    {
      html: '<article><style>img { width:900px !important }</style><img alt="" style="width:16px;height:1px" data-jithub-layout-spacer-index="3"></article>',
      spacers: [snapshotSpacer],
      hidden: [],
      error: /changed layout-spacer CSS or rendered geometry after attachment/u,
    },
  ]) {
    const rejected = await cdp.send("Runtime.callFunctionOn", {
      objectId: omissionWindow.result.objectId,
      functionDeclaration: replayFunctionDeclaration(),
      arguments: [fixture.html, [], fixture.spacers, fixture.hidden,
        replayServer.baseUrl, 15_000, MAX_SAME_BYTE_TRAVERSAL_VIEWPORTS,
        SAME_BYTE_TRAVERSAL_VIEWPORT_STEP_RATIO].map(value => ({ value })),
      awaitPromise: true,
      returnByValue: true,
    });
    assert.match(rejected.exceptionDetails?.exception?.description || "", fixture.error);
  }

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
  assert.equal(replay.assets.cssHiddenImageOmissionCount, 1);
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
