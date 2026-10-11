import assert from "node:assert/strict";
import test from "node:test";
import { createUnavailableVisibleImageEvidence } from "./same-byte-visible-image-diagnostics.mjs";

test("unavailable live-image evidence is bounded, hashed, and does not waive capture", () => {
  const source = "https://example.invalid/private/image.png";
  const alt = "private README text";
  const evidence = createUnavailableVisibleImageEvidence([
    { replayIndex: 0, source, alt, complete: true, naturalWidth: 10, naturalHeight: 20,
      renderedWidth: 10, renderedHeight: 20 },
    { replayIndex: 1, source, alt, complete: true, naturalWidth: 0, naturalHeight: 0,
      renderedWidth: 10, renderedHeight: 20 },
    { replayIndex: 2, source: "data:image/png;base64,AA", complete: false,
      naturalWidth: 0, naturalHeight: 0, renderedWidth: 1, renderedHeight: 1 },
  ]);
  assert.equal(evidence.visibleImageCount, 3);
  assert.equal(evidence.unavailableImageCount, 2);
  assert.deepEqual(evidence.failures.map(failure => failure.replayIndex), [1, 2]);
  assert.match(evidence.failures[0].sourceSha256, /^[0-9a-f]{64}$/u);
  assert.equal(evidence.failures[0].sourceKind, "http");
  assert.equal(evidence.failures[1].sourceKind, "data");
  assert.equal(JSON.stringify(evidence).includes(source), false);
  assert.equal(JSON.stringify(evidence).includes(alt), false);
  assert.equal(evidence.failures[1].complete, false);
  assert.equal(createUnavailableVisibleImageEvidence(new Array(15_001)), null);
  assert.equal(createUnavailableVisibleImageEvidence([null]), null);
});
