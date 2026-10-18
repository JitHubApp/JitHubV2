import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  COMMON_SUBSET_FIXTURES,
  compareNativeProjections,
  inventoryMarkedSource,
  validateFrozenManifest,
  verifyFrozenReadmeBytes,
} from "./correctness-source-inventory.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const markedPath = path.join(here, "vendor", "marked-18.0.5.umd.js");
const markedBytes = await readFile(markedPath);
const marked = require(markedPath).marked;
const sha256 = bytes => createHash("sha256").update(bytes).digest("hex");

test("pinned Marked parser license and hash match the inventory contract", async () => {
  assert.equal(sha256(markedBytes), "2dc4769dfde29f51c7aca1a539c6407c789c8ea644cf8b7d01ded28a9c1d800b");
  assert.match(await readFile(path.join(here, "vendor", "marked-18.0.5.LICENSE"), "utf8"), /MIT license/u);
});

test("fixture-tested source tokens have exact UTF-16 spans and structured fields", () => {
  const headingAndCode = COMMON_SUBSET_FIXTURES[0];
  assert.equal(headingAndCode.id, "utf16-prefix-atx-heading-fenced-code");
  const first = inventoryMarkedSource(headingAndCode.source, marked);
  assert.equal(first.status, "complete");
  assert.equal(first.sourceTokenCoverage.status, "exact");
  assert.equal(first.projections.headings.length, 1);
  assert.equal(first.projections.codeBlocks.length, 1);
  assert.equal(first.projections.links.length, 0);
  assert.equal(first.projections.images.length, 0);
  assert.equal(first.projections.headings[0].sourceStart, headingAndCode.source.indexOf("# Plain heading"));
  assert.equal(first.projections.headings[0].sourceLength, "# Plain heading".length);
  assert.equal(first.projections.headings[0].displayTextSha256, sha256(Buffer.from("Plain heading", "utf8")));
  assert.equal(first.projections.codeBlocks[0].sourceStart, headingAndCode.source.indexOf("\x60\x60\x60csharp"));
  assert.equal(headingAndCode.source.slice(
    first.projections.codeBlocks[0].sourceStart,
    first.projections.codeBlocks[0].sourceStart + first.projections.codeBlocks[0].sourceLength,
  ).endsWith("\x60\x60\x60\n"), true, "Marked raw includes the line terminator after the closing fence.");
  assert.equal(first.projections.codeBlocks[0].language, "csharp");
  assert.equal(first.projections.codeBlocks[0].displayTextSha256, sha256(Buffer.from("var answer = 42;", "utf8")));

  const linksAndImages = COMMON_SUBSET_FIXTURES[1];
  assert.equal(linksAndImages.id, "direct-text-link-and-image");
  const second = inventoryMarkedSource(linksAndImages.source, marked);
  assert.equal(second.status, "complete");
  assert.equal(second.sourceTokenCoverage.status, "exact");
  assert.equal(second.projections.links.length, 1);
  assert.equal(second.projections.images.length, 1);
  assert.equal(second.projections.links[0].sourceStart, linksAndImages.source.indexOf("[plain]"));
  assert.equal(second.projections.links[0].sourceLength, "[plain](https://example.test/path)".length);
  assert.equal(second.projections.links[0].urlSha256, sha256(Buffer.from("https://example.test/path", "utf8")));
  assert.equal(second.projections.images[0].sourceStart, linksAndImages.source.indexOf("![diagram]"));
  assert.equal(second.projections.images[0].sourceLength, "![diagram](images/diagram.png)".length);
  assert.equal(second.projections.images[0].altTextSha256, sha256(Buffer.from("diagram", "utf8")));
  assert.equal(second.projections.images[0].isInline, true);

  const codeAtEof = COMMON_SUBSET_FIXTURES[2];
  assert.equal(codeAtEof.id, "fenced-code-at-eof-without-line-terminator");
  const third = inventoryMarkedSource(codeAtEof.source, marked);
  assert.equal(third.status, "complete");
  assert.equal(third.projections.codeBlocks.length, 1);
  assert.equal(third.projections.codeBlocks[0].sourceLength, codeAtEof.source.length);
  assert.equal(codeAtEof.source.slice(
    third.projections.codeBlocks[0].sourceStart,
    third.projections.codeBlocks[0].sourceStart + third.projections.codeBlocks[0].sourceLength,
  ), codeAtEof.source);
});

test("raw HTML, extensions, formatted headings, and reference links remain explicit evidence", () => {
  const source =
    "# Formatted **heading**\n\n[reference][target]\n\n[target]: https://example.test\n\n" +
    "\x60\x60\x60mermaid\ngraph TD\nA --> B\n\x60\x60\x60\n";
  const inventory = inventoryMarkedSource(source, marked);
  assert.equal(inventory.status, "complete");
  assert.equal(inventory.sourceTokenCoverage.status, "exact");
  assert.equal(inventory.projections.headings.length, 0);
  assert.equal(inventory.projections.links.length, 0);
  assert.equal(inventory.projections.codeBlocks.length, 0);
  assert.ok(inventory.uncomparedTopLevelTokens.some(item => item.exclusionReason === "mermaid-extension-fence"));
  assert.ok(inventory.uncomparedTopLevelTokens.some(item => item.exclusionReason === "not-plain-fixture-tested-atx-heading"));
  assert.ok(inventory.excludedInlineEvidence.some(item =>
    item.reason === "not-fixture-tested-direct-plain-link"));
  assert.ok(inventory.uncomparedTopLevelTokens.every(item =>
    /^[0-9a-f]{64}$/u.test(item.rawSha256) && Number.isInteger(item.sourceStart)));

  const rawHtml = inventoryMarkedSource("<div>raw</div>\n", marked);
  assert.ok(rawHtml.uncomparedTopLevelTokens.some(item => item.exclusionReason === "raw-html"));
  assert.equal(rawHtml.rawHtmlContainerScope.status, "partial");
});

test("GitHub emoji shortcodes and HTML entities are explicit heading exclusions", () => {
  const source = "# :smile:\n\n# &copy;\n\n# Plain heading\n\nparagraph\n";
  const inventory = inventoryMarkedSource(source, marked);
  assert.equal(inventory.status, "complete");
  assert.deepEqual(inventory.projections.headings.map(item => item.displayTextUtf16Length), ["Plain heading".length]);
  assert.ok(inventory.uncomparedTopLevelTokens.some(item =>
    item.exclusionReason === "github-emoji-shortcode-expansion"));
  assert.ok(inventory.uncomparedTopLevelTokens.some(item =>
    item.exclusionReason === "github-html-entity-text-expansion"));
});

test("native comparison filters only source-proven common-subset entries and classifies mismatches", () => {
  const fixture = COMMON_SUBSET_FIXTURES[0];
  const comparisonSource = fixture.source + "\n# Other **heading**\n";
  const inventory = inventoryMarkedSource(comparisonSource, marked);
  const native = {
    documentSourceMatchesInput: true,
    rawSourceSha256: inventory.sourceTokenCoverage.sourceSha256,
    headingProjections: [...inventory.projections.headings],
    codeBlockProjections: [...inventory.projections.codeBlocks],
    linkProjections: [],
    imageProjections: [],
  };
  // A formatted heading is a native projection but outside the admitted
  // source syntax. It must be counted as excluded evidence, not compared.
  const formattedSource = "# Other **heading**";
  native.headingProjections.push({
    sourceStart: comparisonSource.indexOf(formattedSource),
    sourceLength: formattedSource.length,
    level: 1,
    displayTextSha256: sha256(Buffer.from("Other heading", "utf8")),
    displayTextUtf16Length: "Other heading".length,
  });
  const match = compareNativeProjections(inventory, native, comparisonSource);
  assert.equal(match.status, "matched-common-subset");
  assert.equal(match.excludedNativeProjectionCounts.headings, 1);
  native.headingProjections[0] = { ...native.headingProjections[0], level: 2 };
  const mismatch = compareNativeProjections(inventory, native, comparisonSource);
  assert.equal(mismatch.status, "mismatch-requires-classification");
  assert.equal(
    mismatch.mismatches[0].classification,
    "requires-human-classification-not-automatically-a-renderer-defect",
  );
  assert.notEqual(mismatch.status, "renderer-defect");
});

test("comparison pairs by exact source identity and preserves missing plus unexpected evidence", () => {
  const source = "# A\n\n# B\n\n# C\n\nparagraph\n";
  const inventory = inventoryMarkedSource(source, marked);
  const [a, b, c] = inventory.projections.headings;
  const native = {
    documentSourceMatchesInput: true,
    rawSourceSha256: inventory.sourceTokenCoverage.sourceSha256,
    headingProjections: [a, c, { ...c }],
    codeBlockProjections: [],
    linkProjections: [],
    imageProjections: [],
  };
  const comparison = compareNativeProjections(inventory, native, source);
  assert.equal(comparison.status, "mismatch-requires-classification");
  assert.equal(comparison.candidateElementCount, 3);
  assert.equal(comparison.comparedElementCount, 2);
  const headingMismatch = comparison.mismatches.find(item => item.projection === "headings");
  assert.equal(headingMismatch.missingNativeCount, 1);
  assert.equal(headingMismatch.unexpectedNativeCount, 1);
  assert.equal(headingMismatch.firstDifference.missingMarkedCandidate.sourceStart, b.sourceStart);
  assert.equal(headingMismatch.firstDifference.unexpectedNativeProjection.sourceStart, c.sourceStart);
});

test("code fence source-span newline convention is recorded and still compares semantics", () => {
  const source = COMMON_SUBSET_FIXTURES[0].source;
  const inventory = inventoryMarkedSource(source, marked);
  const markedCode = inventory.projections.codeBlocks[0];
  const native = {
    documentSourceMatchesInput: true,
    rawSourceSha256: inventory.sourceTokenCoverage.sourceSha256,
    headingProjections: inventory.projections.headings,
    codeBlockProjections: [{ ...markedCode, sourceLength: markedCode.sourceLength - 1 }],
    linkProjections: [],
    imageProjections: [],
  };
  const comparison = compareNativeProjections(inventory, native, source);
  assert.equal(comparison.status, "matched-common-subset-with-source-span-conventions");
  assert.equal(comparison.candidateElementCount, 2);
  assert.equal(comparison.comparedElementCount, 2);
  assert.equal(comparison.sourceSpanConventionDifferenceCount, 1);
  assert.deepEqual(comparison.sourceSpanConventionDifferences[0], {
    projection: "codeBlocks",
    sourceStart: markedCode.sourceStart,
    markedSourceLength: markedCode.sourceLength,
    nativeSourceLength: markedCode.sourceLength - 1,
    lineTerminator: "\n",
  });
});

test("empty common subset is explicitly not compared, not a vacuous match", () => {
  const source = "<div>raw HTML only</div>\n";
  const inventory = inventoryMarkedSource(source, marked);
  const native = {
    documentSourceMatchesInput: true,
    rawSourceSha256: inventory.sourceTokenCoverage.sourceSha256,
    headingProjections: [],
    codeBlockProjections: [],
    linkProjections: [],
    imageProjections: [],
  };
  const comparison = compareNativeProjections(inventory, native, source);
  assert.equal(comparison.status, "not-compared-no-common-subset-elements");
  assert.equal(comparison.candidateElementCount, 0);
  assert.equal(comparison.comparedElementCount, 0);
});

test("comparison refuses a supplied source string that differs from the inventoried bytes", () => {
  const source = "# Original";
  const inventory = inventoryMarkedSource(source, marked);
  const native = {
    documentSourceMatchesInput: true,
    rawSourceSha256: inventory.sourceTokenCoverage.sourceSha256,
    headingProjections: inventory.projections.headings,
    codeBlockProjections: [],
    linkProjections: [],
    imageProjections: [],
  };
  assert.equal(
    compareNativeProjections(inventory, native, "# DifferenX").status,
    "not-compared-supplied-source-hash-mismatch",
  );
  assert.equal(compareNativeProjections(inventory, native, source).status, "matched-common-subset");
});

test("table cell inline token trees count toward bounded traversal", () => {
  const columns = 1000;
  const source = "|" + Array(columns).fill("h").join("|") + "|\n|" +
    Array(columns).fill("---").join("|") + "|\n|" + Array(columns).fill("c").join("|") + "|\n";
  const inventory = inventoryMarkedSource(source, marked);
  assert.equal(inventory.status, "complete");
  assert.ok(inventory.tokenCount > columns * 2, "table cells and their inline token nodes are traversed");
  assert.ok(inventory.tokenTypeCounts.unknown >= columns * 2, "cell wrapper nodes are counted as unknown structured nodes");
  assert.ok(inventory.tokenTypeCounts.text >= columns * 2, "cell inline tokens are traversed under the global token bound");
});

test("source-token coverage failures are explicit and cannot yield comparison candidates", () => {
  const source = "original source";
  const incomplete = inventoryMarkedSource(source, {
    lexer: () => [{ type: "paragraph", raw: "not the supplied source", tokens: [] }],
  });
  assert.equal(incomplete.status, "incomplete");
  assert.equal(incomplete.reason, "marked-top-level-token-raw-concatenation-does-not-equal-source");
  assert.equal(incomplete.projections.headings.length, 0);
  assert.equal(incomplete.projections.codeBlocks.length, 0);
  assert.equal(incomplete.uncomparedTopLevelTokens.length, 1);
});

test("native link and image projections inside non-paragraph containers are excluded", () => {
  const source =
    "## ![heading image](heading.png)\n\n- ![list image](list.png)\n\n![plain](plain.png)\n";
  const inventory = inventoryMarkedSource(source, marked);
  const native = {
    documentSourceMatchesInput: true,
    rawSourceSha256: inventory.sourceTokenCoverage.sourceSha256,
    headingProjections: [],
    codeBlockProjections: [],
    linkProjections: [],
    imageProjections: [
      {
        sourceStart: source.indexOf("![heading image]"),
        sourceLength: "![heading image](heading.png)".length,
      },
      {
        sourceStart: source.indexOf("![list image]"),
        sourceLength: "![list image](list.png)".length,
      },
      ...inventory.projections.images,
    ],
  };
  const comparison = compareNativeProjections(inventory, native, source);
  assert.equal(comparison.status, "matched-common-subset");
  assert.equal(comparison.excludedNativeProjectionCounts.images, 2);
});

test("native links with formatted labels remain excluded by inline evidence", () => {
  const source = "[_formatted_](https://example.test/path)\n\nplain text\n";
  const inventory = inventoryMarkedSource(source, marked);
  assert.equal(inventory.projections.links.length, 0);
  assert.ok(inventory.excludedInlineEvidence.some(item =>
    item.reason === "not-fixture-tested-direct-plain-link" && item.sourceStart === 0));
  const native = {
    documentSourceMatchesInput: true,
    rawSourceSha256: inventory.sourceTokenCoverage.sourceSha256,
    headingProjections: [],
    codeBlockProjections: [],
    linkProjections: [{
      sourceStart: 0,
      sourceLength: "[_formatted_](https://example.test/path)".length,
      displayTextSha256: sha256(Buffer.from("formatted", "utf8")),
      displayTextUtf16Length: "formatted".length,
      urlSha256: sha256(Buffer.from("https://example.test/path", "utf8")),
      urlUtf16Length: "https://example.test/path".length,
      titleSha256: null,
      titleUtf16Length: null,
    }],
    imageProjections: [],
  };
  const comparison = compareNativeProjections(inventory, native, source);
  assert.equal(comparison.status, "not-compared-no-common-subset-elements");
  assert.equal(comparison.excludedNativeProjectionCounts.links, 1);
});

test("Markdown-looking tokens nested in raw HTML containers are outside the common subset", () => {
  const source = "<details>\n<summary>\n\n```csharp\nvar value = 1;\n```\n</summary>\n</details>\n";
  const inventory = inventoryMarkedSource(source, marked);
  assert.equal(inventory.status, "complete");
  assert.equal(inventory.projections.codeBlocks.length, 0);
  assert.ok(inventory.uncomparedTopLevelTokens.some(item =>
    item.tokenType === "code" && item.exclusionReason === "inside-raw-html-container"));
  const native = {
    documentSourceMatchesInput: true,
    rawSourceSha256: inventory.sourceTokenCoverage.sourceSha256,
    headingProjections: [],
    codeBlockProjections: [{
      sourceStart: source.indexOf("```csharp"),
      sourceLength: "```csharp\nvar value = 1;\n```".length,
      language: "csharp",
      displayTextSha256: sha256(Buffer.from("var value = 1;", "utf8")),
      displayTextUtf16Length: "var value = 1;".length,
    }],
    linkProjections: [],
    imageProjections: [],
  };
  const comparison = compareNativeProjections(inventory, native, source);
  assert.equal(comparison.status, "not-compared-no-common-subset-elements");
  assert.equal(comparison.excludedNativeProjectionCounts.codeBlocks, 1);
});

test("raw-text HTML cannot spoof a container close-tag boundary", () => {
  for (const rawTextTag of ["script", "style", "textarea"]) {
    const source = "<div>\n\n<" + rawTextTag + ">const x = \"</div>\";</" + rawTextTag +
      ">\n\n# Common heading\n\n</div>\n";
    const inventory = inventoryMarkedSource(source, marked);
    assert.equal(inventory.status, "complete", rawTextTag);
    assert.equal(inventory.rawHtmlContainerScope.status, "partial", rawTextTag);
    assert.equal(
      inventory.rawHtmlContainerScope.reason,
      "raw-html-markup-not-standalone-attribute-free-tag-lines",
      rawTextTag,
    );
    assert.equal(inventory.projections.headings.length, 0, rawTextTag);
    assert.ok(inventory.uncomparedTopLevelTokens.some(item =>
      item.tokenType === "heading" && item.exclusionReason === "raw-html-container-scope-ambiguous"), rawTextTag);
  }
});

test("comparison refuses to treat a parse exception or missing projections as a match", () => {
  const inventory = inventoryMarkedSource(COMMON_SUBSET_FIXTURES[0].source, marked);
  assert.equal(
    compareNativeProjections(inventory, { documentSourceMatchesInput: false }, COMMON_SUBSET_FIXTURES[0].source).status,
    "not-compared-native-parse-not-successful",
  );
  assert.equal(
    compareNativeProjections(inventory, {
      documentSourceMatchesInput: true,
      rawSourceSha256: inventory.sourceTokenCoverage.sourceSha256,
    }, COMMON_SUBSET_FIXTURES[0].source).status,
    "not-compared-native-projections-not-exported",
  );
});

test("frozen manifest structure enforces exactly 500 immutable unique identities", () => {
  const repositories = Array.from({ length: 500 }, (_, index) => ({
    rank: index + 1,
    id: index + 1,
    fullName: "owner/repo" + (index + 1),
    commitSha: "a".repeat(40),
    readme: {
      available: true,
      byteSize: 0,
      path: "README.md",
      sha: index.toString(16).padStart(40, "0"),
    },
  }));
  const manifest = { schemaVersion: 2, requestedCount: 500, repositories };
  assert.equal(validateFrozenManifest(manifest), true);
  assert.throws(() => validateFrozenManifest({
    ...manifest,
    repositories: repositories.map((repository, index) => ({
      ...repository,
      rank: index === 2 ? 2 : repository.rank,
    })),
  }), /Invalid frozen repository/u);
  assert.throws(() => validateFrozenManifest({
    ...manifest,
    repositories: repositories.map((repository, index) => ({
      ...repository,
      readme: index === 0 ? { ...repository.readme, path: "../README.md" } : repository.readme,
    })),
  }), /Invalid frozen repository/u);
});

test("source bytes require exact declared size and Git blob identity", () => {
  const bytes = Buffer.from("fixture source\n", "utf8");
  const gitBlobSha = createHash("sha1")
    .update(Buffer.from("blob " + bytes.length + "\0", "ascii"))
    .update(bytes)
    .digest("hex");
  assert.equal(verifyFrozenReadmeBytes(bytes, bytes.length, gitBlobSha), true);
  assert.throws(() => verifyFrozenReadmeBytes(bytes, bytes.length + 1, gitBlobSha), /declared size/u);
  assert.throws(() => verifyFrozenReadmeBytes(bytes, bytes.length, "0".repeat(40)), /declared size/u);
});
