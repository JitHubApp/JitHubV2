import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import {
  hasUncapturedExternalInlineSvgResource,
  isSourceLessDecorativeImageSpacerCandidate,
} from "./same-byte-corpus.mjs";
import { pruneClosedDetailsReplayContent } from "./closed-details-snapshot.mjs";

// Share the exact fixture-expression builder between headless and live tests:
// new injected dependencies must be exercised before starting a browser.
export async function readRenderedSnapshotExpression(sourceLessDecorativeImageSpacers = [], cssHiddenImageOmissions = []) {
  const source = await readFile(new URL("./browser-oracle.mjs", import.meta.url), "utf8");
  const functionStart = source.indexOf("async function captureRenderedHtmlSnapshot(");
  assert.notEqual(functionStart, -1, "browser-oracle should expose the snapshot implementation in its source");
  const marker = "const html = await evaluate(cdpClient, `";
  const expressionStart = source.indexOf(marker, functionStart);
  assert.notEqual(expressionStart, -1, "snapshot implementation should evaluate its generated expression");
  const bodyStart = expressionStart + marker.length;
  const bodyEnd = source.indexOf("`);", bodyStart);
  assert.notEqual(bodyEnd, -1, "snapshot expression should have a terminated template literal");
  const templateBody = source.slice(bodyStart, bodyEnd);
  // Recreate JavaScript's template-literal escapes and interpolations exactly
  // as captureRenderedHtmlSnapshot does before sending the string to Edge.
  return new Function(
    "excludedDecorativeImageReplayIndexes",
    "sourceLessDecorativeImageSpacers",
    "cssHiddenImageOmissions",
    "isSourceLessDecorativeImageSpacerCandidate",
    "hasUncapturedExternalInlineSvgResource",
    "pruneClosedDetailsReplayContent",
    `return \`${templateBody}\`;`,
  )([], sourceLessDecorativeImageSpacers, cssHiddenImageOmissions,
    isSourceLessDecorativeImageSpacerCandidate,
    hasUncapturedExternalInlineSvgResource, pruneClosedDetailsReplayContent);
}
