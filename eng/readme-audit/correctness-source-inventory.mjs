import { createHash } from "node:crypto";
import { constants } from "node:fs";
import { lstat, mkdir, open } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const EXPECTED_MANIFEST_SHA256 = "615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72";
const EXPECTED_COUNT = 500;
const MAX_MANIFEST_BYTES = 4 * 1024 * 1024;
const MAX_CASES_BYTES = 64 * 1024 * 1024;
const MAX_README_BYTES = 8 * 1024 * 1024;
const MAX_PARSER_BYTES = 8 * 1024 * 1024;
const MAX_TOKENS = 500_000;
const MAX_DEPTH = 128;
const MAX_EVIDENCE_PER_CASE = 100_000;
const MARKED = Object.freeze({
  name: "marked",
  version: "18.0.5",
  license: "MIT",
  sha256: "2dc4769dfde29f51c7aca1a539c6407c789c8ea644cf8b7d01ded28a9c1d800b",
  options: Object.freeze({ gfm: true, breaks: false, pedantic: false }),
});
const MARKED_PATH = path.join(
  path.dirname(fileURLToPath(import.meta.url)),
  "vendor",
  "marked-18.0.5.umd.js",
);
const require = createRequire(import.meta.url);
const HEX_40 = /^[0-9a-f]{40}$/iu;
const HEX_64 = /^[0-9a-f]{64}$/iu;
const PROJECTIONS = Object.freeze({
  headings: "headingProjections",
  codeBlocks: "codeBlockProjections",
  links: "linkProjections",
  images: "imageProjections",
});

// These named fixture cases mirror AuditSelfTests.cs. Source spans are compared
// only for this deliberately narrow, plain syntax; new syntax needs paired tests.
export const COMMON_SUBSET_FIXTURES = Object.freeze([
  Object.freeze({
    id: "utf16-prefix-atx-heading-fenced-code",
    source: "😀 prefix\n\n# Plain heading\n\n\x60\x60\x60csharp\nvar answer = 42;\n\x60\x60\x60\n",
  }),
  Object.freeze({
    id: "direct-text-link-and-image",
    source: "[plain](https://example.test/path)\n\n![diagram](images/diagram.png)\n",
  }),
  Object.freeze({
    id: "fenced-code-at-eof-without-line-terminator",
    source: "```csharp\nvar answer = 42;\n```",
  }),
]);

export async function createCorrectnessSourceInventory({
  manifestPath,
  sourceObjectRoot,
  nativeCasesPath,
  outputDirectory,
}) {
  const manifestFile = absolutePath(manifestPath, "manifestPath");
  const sourceRoot = absolutePath(sourceObjectRoot, "sourceObjectRoot");
  const casesFile = absolutePath(nativeCasesPath, "nativeCasesPath");
  const output = absolutePath(outputDirectory, "outputDirectory");
  if (await exists(output)) throw new Error("Output directory must be fresh.");
  assertDisjoint(output, manifestFile, "manifest");
  assertDisjoint(output, casesFile, "native cases");

  const objectRoot = await resolveObjectRoot(sourceRoot);
  assertDisjoint(output, objectRoot, "source objects");
  const manifestBytes = await readBoundedFile(manifestFile, MAX_MANIFEST_BYTES);
  const manifestSha256 = sha256(manifestBytes);
  if (manifestSha256 !== EXPECTED_MANIFEST_SHA256) {
    throw new Error("Frozen manifest SHA-256 does not match the pinned top-500 input.");
  }
  const manifest = parseJson(manifestBytes, "frozen manifest");
  validateFrozenManifest(manifest);
  const nativeCases = parseCases(await readBoundedFile(casesFile, MAX_CASES_BYTES));
  const nativeByRank = validateNativeCases(nativeCases, manifest);

  const parserBytes = await readBoundedFile(MARKED_PATH, MAX_PARSER_BYTES);
  if (sha256(parserBytes) !== MARKED.sha256) throw new Error("Vendored Marked SHA-256 mismatch.");
  const marked = require(MARKED_PATH)?.marked;
  if (!marked || typeof marked.lexer !== "function") throw new Error("Pinned Marked lexer unavailable.");

  const rows = [];
  let matched = 0;
  let matchedWithSpanConvention = 0;
  let mismatched = 0;
  let notComparedNoCommonSubset = 0;
  let coverageFailures = 0;
  let completeInventoryCases = 0;
  let rawHtmlScopePartialCases = 0;
  let candidateElementCount = 0;
  let comparedElementCount = 0;
  let uncomparedCount = 0;
  const comparisonStatusCounts = Object.create(null);
  for (const repository of manifest.repositories) {
    const bytes = await readReadmeObject(objectRoot, repository);
    const source = decodeUtf8(bytes, "frozen README rank " + repository.rank);
    const row = createInventoryRow(repository, bytes, source, nativeByRank.get(repository.rank), marked);
    rows.push(JSON.stringify(row));
    if (row.sourceTokenCoverage.status !== "exact") coverageFailures++;
    if (row.status === "complete") {
      completeInventoryCases++;
      uncomparedCount += row.uncomparedTopLevelTokens.filter(item =>
        item.classification === "outside-or-container-for-common-subset").length +
        row.excludedInlineEvidence.length;
    }
    if (row.rawHtmlContainerScope?.status === "partial") rawHtmlScopePartialCases++;
    candidateElementCount += row.nativeComparison.candidateElementCount ?? 0;
    comparedElementCount += row.nativeComparison.comparedElementCount ?? 0;
    comparisonStatusCounts[row.nativeComparison.status] =
      (comparisonStatusCounts[row.nativeComparison.status] ?? 0) + 1;
    if (row.nativeComparison.status === "matched-common-subset") matched++;
    if (row.nativeComparison.status === "matched-common-subset-with-source-span-conventions") {
      matchedWithSpanConvention++;
    }
    if (row.nativeComparison.status === "mismatch-requires-classification") mismatched++;
    if (row.nativeComparison.status === "not-compared-no-common-subset-elements") notComparedNoCommonSubset++;
  }

  const summary = {
    schemaVersion: 1,
    artifactKind: "pinned-markdown-source-parser-inventory",
    interpretation:
      "Diagnostic parser-to-parser comparison only. Mismatches require classification and are not automatically renderer defects. A clean comparison is not a full renderer correctness pass.",
    frozenManifestSha256: manifestSha256,
    manifestRepositoryCount: manifest.repositories.length,
    sourceObjectVerification: "all raw Git blobs revalidated against frozen byte size and Git blob SHA-1 before parsing",
    parser: MARKED,
    commonSubset: [
      "plain ATX headings with direct unescaped text outside raw-HTML containers; HTML entities and GitHub emoji shortcodes are excluded",
      "simple fenced code blocks outside raw-HTML containers; Mermaid fences are excluded",
      "direct inline links with plain unescaped labels outside raw-HTML containers",
      "direct Markdown images with plain unescaped alt text outside raw-HTML containers",
    ],
    excludedContent:
      "For complete inventory cases, every top-level Marked token is retained as type, UTF-16 range if proven, and raw SHA-256. Incomplete cases report the specific traversal-limit or source-coverage reason and are not compared. Raw HTML, extension syntax, containers, and escaped/nested/reference/autolink forms remain explicitly uncompared.",
    sourceTokenCoverageFailureCount: coverageFailures,
    completeSourceInventoryCaseCount: completeInventoryCases,
    rawHtmlContainerScopePartialCaseCount: rawHtmlScopePartialCases,
    matchedCommonSubsetCaseCount: matched,
    matchedWithSourceSpanConventionCaseCount: matchedWithSpanConvention,
    mismatchRequiresClassificationCaseCount: mismatched,
    notComparedNoCommonSubsetCaseCount: notComparedNoCommonSubset,
    candidateElementCount,
    comparedElementCount,
    explicitlyUncomparedEvidenceEntryCount: uncomparedCount,
    nativeComparisonStatusCounts: comparisonStatusCounts,
    casesFile: "cases.jsonl",
  };
  await mkdir(output);
  await writeExclusive(path.join(output, "cases.jsonl"), Buffer.from(rows.join("\n") + "\n", "utf8"));
  await writeExclusive(path.join(output, "summary.json"), Buffer.from(JSON.stringify(summary, null, 2) + "\n", "utf8"));
  return { outputDirectory: output, summary };
}

export function inventoryMarkedSource(source, marked) {
  const tokens = marked.lexer(source, { ...MARKED.options });
  const joinedRaw = tokens.map(token => typeof token?.raw === "string" ? token.raw : "").join("");
  const coverageExact = joinedRaw === source;
  const projections = { headings: [], codeBlocks: [], links: [], images: [] };
  const uncomparedTopLevelTokens = [];
  const excludedInlineEvidence = [];
  const tokenTypeCounts = Object.create(null);
  const rawHtmlContainerScope = { stack: [], ambiguousReason: null, ambiguousSourceStart: null };
  let tokenCount = 0;
  let depthExceeded = false;
  const countTokenTree = (token, depth) => {
    if (!token || typeof token !== "object") return;
    tokenCount++;
    if (tokenCount > MAX_TOKENS) throw new Error("Marked token count exceeds safety bound.");
    if (depth > MAX_DEPTH) {
      depthExceeded = true;
      return;
    }
    const type = typeof token.type === "string" ? token.type : "unknown";
    tokenTypeCounts[type] = (tokenTypeCounts[type] ?? 0) + 1;
    for (const child of children(token)) countTokenTree(child, depth + 1);
  };
  try {
    for (const token of tokens) countTokenTree(token, 0);
  } catch (error) {
    return incompleteInventory(source, joinedRaw, tokenCount, tokenTypeCounts,
      error instanceof Error ? error.message : "Marked token traversal failed.");
  }
  if (depthExceeded) {
    return incompleteInventory(source, joinedRaw, tokenCount, tokenTypeCounts,
      "Marked token nesting exceeds safety bound.");
  }

  let offset = 0;
  for (const token of tokens) {
    const raw = typeof token?.raw === "string" ? token.raw : "";
    const start = coverageExact ? offset : null;
    if (token?.type === "html") updateRawHtmlContainerScope(raw, rawHtmlContainerScope, start);
    const insideRawHtmlContainer = rawHtmlContainerScope.stack.length > 0;
    const rawHtmlScopeIsAmbiguous = rawHtmlContainerScope.ambiguousReason !== null;
    const excludeForHtmlScope = rawHtmlScopeIsAmbiguous || insideRawHtmlContainer;
    const evidence = {
      tokenType: typeof token?.type === "string" ? token.type : "unknown",
      sourceStart: start,
      sourceLength: coverageExact ? raw.length : null,
      rawSha256: sha256(Buffer.from(raw, "utf8")),
      rawUtf16Length: raw.length,
      classification: "outside-or-container-for-common-subset",
    };
    if (coverageExact) offset += raw.length;

    if (token?.type === "heading") {
      const projection = excludeForHtmlScope ? null : projectPlainAtxHeading(token, start, coverageExact);
      if (projection) {
        projections.headings.push(projection);
        evidence.classification = "projected-common-subset";
      } else {
        evidence.exclusionReason = excludeForHtmlScope
          ? rawHtmlScopeIsAmbiguous ? "raw-html-container-scope-ambiguous" : "inside-raw-html-container"
          : headingExclusionReason(token, start, coverageExact);
      }
    } else if (token?.type === "code") {
      const projection = excludeForHtmlScope ? null : projectPlainFencedCode(token, start, coverageExact);
      if (projection) {
        projections.codeBlocks.push(projection);
        evidence.classification = "projected-common-subset";
      } else {
        evidence.exclusionReason = excludeForHtmlScope
          ? rawHtmlScopeIsAmbiguous ? "raw-html-container-scope-ambiguous" : "inside-raw-html-container"
          : typeof token.lang === "string" && token.lang.trim().toLowerCase() === "mermaid"
            ? "mermaid-extension-fence"
            : "not-fixture-tested-simple-fenced-code";
      }
    } else if (token?.type === "paragraph" && coverageExact) {
      if (excludeForHtmlScope) {
        evidence.exclusionReason = rawHtmlScopeIsAmbiguous
          ? "raw-html-container-scope-ambiguous"
          : "inside-raw-html-container";
      } else {
        evidence.classification = "container-for-inline-projections";
        collectInlineTokens(token.tokens, raw, start, projections, excludedInlineEvidence);
      }
    } else {
      evidence.exclusionReason = token?.type === "html"
        ? "raw-html"
        : token?.type === "table" || token?.type === "list" || token?.type === "blockquote"
          ? "container-not-in-common-subset"
          : token?.type === "space"
            ? "source-whitespace-not-projected"
            : "token-type-not-in-common-subset";
    }
    uncomparedTopLevelTokens.push(evidence);
    if (uncomparedTopLevelTokens.length > MAX_EVIDENCE_PER_CASE) {
      return incompleteInventory(source, joinedRaw, tokenCount, tokenTypeCounts,
        "Top-level evidence count exceeds safety bound.");
    }
  }
  const status = coverageExact && offset === source.length ? "complete" : "incomplete";
  const rawHtmlScopeStatus = rawHtmlContainerScope.ambiguousReason !== null || rawHtmlContainerScope.stack.length > 0
    ? "partial"
    : "exact";
  return {
    status,
    ...(status === "incomplete"
      ? { reason: "marked-top-level-token-raw-concatenation-does-not-equal-source" }
      : {}),
    tokenCount,
    tokenTypeCounts,
    sourceTokenCoverage: coverage(source, joinedRaw, status === "complete"),
    rawHtmlContainerScope: {
      status: rawHtmlScopeStatus,
      reason: rawHtmlContainerScope.ambiguousReason ??
        (rawHtmlContainerScope.stack.length > 0 ? "open-raw-html-container-at-end-of-source" : null),
      sourceStart: rawHtmlContainerScope.ambiguousSourceStart,
      openContainersAtEnd: [...rawHtmlContainerScope.stack],
    },
    projections,
    uncomparedTopLevelTokens,
    excludedInlineEvidence,
  };
}

function incompleteInventory(source, joinedRaw, tokenCount, tokenTypeCounts, reason) {
  return {
    status: "incomplete",
    reason,
    tokenCount,
    tokenTypeCounts,
    sourceTokenCoverage: coverage(source, joinedRaw, false),
    rawHtmlContainerScope: { status: "not-computed-incomplete-inventory", reason: "inventory-incomplete" },
    projections: { headings: [], codeBlocks: [], links: [], images: [] },
    uncomparedTopLevelTokens: [],
    excludedInlineEvidence: [],
  };
}

function createInventoryRow(repository, bytes, source, native, marked) {
  const inventory = inventoryMarkedSource(source, marked);
  return {
    rank: repository.rank,
    repositoryId: repository.id,
    fullName: repository.fullName,
    commitSha: repository.commitSha.toLowerCase(),
    readmePath: repository.readme.path,
    readmeGitBlobSha1: repository.readme.sha.toLowerCase(),
    readmeByteSize: bytes.length,
    rawSha256: sha256(bytes),
    parser: { name: MARKED.name, version: MARKED.version, sha256: MARKED.sha256, options: MARKED.options },
    ...inventory,
    nativeComparison: compareNativeProjections(inventory, native, source),
  };
}

function projectPlainAtxHeading(token, sourceStart, spansProven) {
  const match = /^#{1,6} ([^\r\n#][^\r\n]*)$/u.exec(token.raw);
  const inline = Array.isArray(token.tokens) ? token.tokens : [];
  if (!spansProven || !Number.isInteger(sourceStart) || !match ||
      containsGithubTextExtension(match[1]) ||
      inline.length !== 1 || inline[0]?.type !== "text" ||
      inline[0].raw !== inline[0].text || inline[0].escaped === true ||
      token.depth < 1 || token.depth > 6 || inline[0].text !== match[1]) return null;
  return {
    sourceStart,
    sourceLength: token.raw.length,
    level: token.depth,
    displayTextSha256: hashText(inline[0].text),
    displayTextUtf16Length: inline[0].text.length,
  };
}

function headingExclusionReason(token, sourceStart, spansProven) {
  const match = /^#{1,6} ([^\r\n#][^\r\n]*)$/u.exec(typeof token?.raw === "string" ? token.raw : "");
  const inline = Array.isArray(token?.tokens) ? token.tokens : [];
  if (spansProven && match && containsGithubTextExtension(match[1]) &&
      inline.length === 1 && inline[0]?.type === "text" && inline[0].raw === inline[0].text &&
      inline[0].escaped !== true && inline[0].text === match[1]) {
    return /&(?:#(?:\d+|x[\da-f]+)|[a-z][a-z\d]+);/iu.test(match[1])
      ? "github-html-entity-text-expansion"
      : "github-emoji-shortcode-expansion";
  }
  return "not-plain-fixture-tested-atx-heading";
}

function containsGithubTextExtension(value) {
  return /&(?:#(?:\d+|x[\da-f]+)|[a-z][a-z\d]+);/iu.test(value) ||
    /:[a-z0-9_+-]{1,64}:/iu.test(value);
}

// This deliberately is not a general HTML parser. It recognizes only
// standalone, attribute-free tag lines; any other raw HTML makes the remaining
// document scope ambiguous and therefore explicitly uncompared.
function updateRawHtmlContainerScope(raw, state, sourceStart) {
  if (state.ambiguousReason !== null) return;
  const voidElements = new Set([
    "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
  ]);
  const tagLine = /^<(\/)?([a-z][a-z\d:-]*)\s*>$/iu;
  let offset = 0;
  for (const rawLine of raw.split(/(?<=\n)/u)) {
    const line = rawLine.replace(/\r?\n$/u, "").trim();
    if (line.length === 0) { offset += rawLine.length; continue; }
    const match = tagLine.exec(line);
    if (!match) {
      // Do not scan through raw-text elements, comments, attributes, or mixed
      // HTML/text. A close-tag-looking string there is not reliable evidence.
      state.ambiguousReason = "raw-html-markup-not-standalone-attribute-free-tag-lines";
      state.ambiguousSourceStart = Number.isSafeInteger(sourceStart) ? sourceStart + offset : null;
      return;
    }
    const tagName = match[2].toLowerCase();
    if (match[1]) {
      if (state.stack.at(-1) !== tagName) {
        state.ambiguousReason = "raw-html-close-tag-does-not-match-known-container-stack";
        state.ambiguousSourceStart = Number.isSafeInteger(sourceStart) ? sourceStart + offset : null;
        return;
      }
      state.stack.pop();
    } else if (!voidElements.has(tagName)) {
      state.stack.push(tagName);
    }
    offset += rawLine.length;
  }
}

function projectPlainFencedCode(token, sourceStart, spansProven) {
  const match = /^\x60{3}([A-Za-z0-9_.+-]*)\r?\n([\s\S]*?)\r?\n\x60{3}\r?\n?$/u.exec(token.raw);
  const language = typeof token.lang === "string" ? token.lang.trim() : "";
  if (!spansProven || !Number.isInteger(sourceStart) || !match ||
      (language !== "" && !/^[A-Za-z0-9_.+-]+$/u.test(language)) ||
      language.toLowerCase() === "mermaid") return null;
  const text = normalizeCodeText(typeof token.text === "string" ? token.text : "");
  return {
    sourceStart,
    sourceLength: token.raw.length,
    language: language || null,
    displayTextSha256: hashText(text),
    displayTextUtf16Length: text.length,
  };
}

function collectInlineTokens(tokens, parentRaw, parentStart, projections, excluded) {
  if (!Array.isArray(tokens) || typeof parentRaw !== "string" || !Number.isInteger(parentStart)) return;
  let cursor = 0;
  for (const token of tokens) {
    const raw = typeof token?.raw === "string" ? token.raw : "";
    const relativeStart = raw.length > 0 ? parentRaw.indexOf(raw, cursor) : -1;
    if (relativeStart < 0) {
      recordExcluded(excluded, token, null, "inline-token-raw-not-locatable");
      continue;
    }
    cursor = relativeStart + raw.length;
    const start = parentStart + relativeStart;
    if (token.type === "link" || token.type === "image") {
      const projection = projectPlainDirect(token, start);
      if (projection) projections[projection.kind === "link" ? "links" : "images"].push(projection.value);
      else recordExcluded(excluded, token, start,
        token.type === "link"
          ? "not-fixture-tested-direct-plain-link"
          : "not-fixture-tested-direct-plain-image");
    } else if (token.type === "html") {
      recordExcluded(excluded, token, start, "raw-html-inline");
    } else if (token.type !== "text" || token.raw !== token.text || token.escaped === true) {
      recordExcluded(excluded, token, start, "escaped-or-formatted-inline-syntax");
    }
    if (Array.isArray(token.tokens)) {
      collectInlineTokens(token.tokens, raw, start, projections, excluded);
    }
    if (excluded.length > MAX_EVIDENCE_PER_CASE) throw new Error("Inline evidence count exceeds safety bound.");
  }
}

function projectPlainDirect(token, sourceStart) {
  const raw = token.raw;
  const text = typeof token.text === "string" ? token.text : "";
  const inline = Array.isArray(token.tokens) ? token.tokens : [];
  const syntax = token.type === "image"
    ? /^!\[([\p{L}\p{N} ._-]+)\]\(([^()\s<>\\]+)\)$/u
    : /^\[([\p{L}\p{N} ._-]+)\]\(([^()\s<>\\]+)\)$/u;
  const match = syntax.exec(raw);
  if (!match || match[1] !== text || match[2] !== token.href || token.title != null ||
      inline.length !== 1 || inline[0]?.type !== "text" ||
      inline[0].raw !== inline[0].text || inline[0].escaped === true ||
      inline[0].text !== text) return null;
  const fields = {
    sourceStart,
    sourceLength: raw.length,
    displayTextSha256: hashText(text),
    displayTextUtf16Length: text.length,
    urlSha256: hashText(token.href),
    urlUtf16Length: token.href.length,
    titleSha256: null,
    titleUtf16Length: null,
  };
  return token.type === "link"
    ? { kind: "link", value: fields }
    : { kind: "image", value: { ...fields, altTextSha256: hashText(text), altTextUtf16Length: text.length, isInline: true } };
}

function recordExcluded(target, token, sourceStart, reason) {
  if (target.length >= MAX_EVIDENCE_PER_CASE) throw new Error("Inline evidence count exceeds safety bound.");
  const raw = typeof token?.raw === "string" ? token.raw : "";
  target.push({
    tokenType: typeof token?.type === "string" ? token.type : "unknown",
    sourceStart,
    sourceLength: sourceStart == null ? null : raw.length,
    rawSha256: hashText(raw),
    rawUtf16Length: raw.length,
    reason,
  });
}

export function compareNativeProjections(inventory, native, source) {
  if (inventory.status !== "complete" || inventory.sourceTokenCoverage.status !== "exact") {
    return { status: "not-compared-inventory-incomplete", mismatches: [] };
  }
  if (typeof source !== "string" ||
      sha256(Buffer.from(source, "utf8")) !== inventory.sourceTokenCoverage.sourceSha256) {
    return { status: "not-compared-supplied-source-hash-mismatch", mismatches: [] };
  }
  if (native?.documentSourceMatchesInput !== true) {
    return { status: "not-compared-native-parse-not-successful", mismatches: [] };
  }
  if (typeof native.rawSourceSha256 !== "string" ||
      native.rawSourceSha256.toLowerCase() !== inventory.sourceTokenCoverage.sourceSha256) {
    return { status: "not-compared-native-source-hash-mismatch", mismatches: [] };
  }
  const mismatches = [];
  const excludedNativeCounts = {};
  let candidateElementCount = 0;
  let comparedElementCount = 0;
  let spanConventionDifferenceCount = 0;
  const spanConventionDifferences = [];
  for (const [name, property] of Object.entries(PROJECTIONS)) {
    const nativeValues = native[property];
    if (!Array.isArray(nativeValues)) {
      return { status: "not-compared-native-projections-not-exported", mismatches: [] };
    }
    const markedValues = inventory.projections[name];
    candidateElementCount += markedValues.length;
    const bySpan = new Map();
    for (const value of nativeValues) {
      const key = projectionSpanKey(value);
      if (key === null) continue;
      const values = bySpan.get(key) ?? [];
      values.push(value);
      bySpan.set(key, values);
    }
    const matchedNative = new Set();
    const missing = [];
    const semanticDifferences = [];
    const conventions = [];
    for (const markedValue of markedValues) {
      const key = projectionSpanKey(markedValue);
      const exact = key === null ? null : bySpan.get(key)?.find(value => !matchedNative.has(value));
      if (exact) {
        matchedNative.add(exact);
        comparedElementCount++;
        if (canonicalJson(projectionSemantics(name, markedValue)) !==
            canonicalJson(projectionSemantics(name, exact))) {
          semanticDifferences.push({ marked: markedValue, native: exact });
        }
        continue;
      }

      const conventionMatch = name === "codeBlocks"
        ? findCodeSpanConvention(markedValue, nativeValues, matchedNative, source)
        : null;
      if (conventionMatch) {
        matchedNative.add(conventionMatch.native);
        comparedElementCount++;
        spanConventionDifferenceCount++;
        const detail = {
          projection: name,
          sourceStart: markedValue.sourceStart,
          markedSourceLength: markedValue.sourceLength,
          nativeSourceLength: conventionMatch.native.sourceLength,
          lineTerminator: conventionMatch.lineTerminator,
        };
        conventions.push(detail);
        if (spanConventionDifferences.length < 32) spanConventionDifferences.push(detail);
        if (canonicalJson(projectionSemantics(name, markedValue)) !==
            canonicalJson(projectionSemantics(name, conventionMatch.native))) {
          semanticDifferences.push({ marked: markedValue, native: conventionMatch.native });
        }
        continue;
      }
      missing.push(markedValue);
    }

    let excludedCount = 0;
    const unexpected = [];
    for (const value of nativeValues) {
      if (matchedNative.has(value)) continue;
      if (isNativeCommonSubsetEntry(name, value, source, inventory.uncomparedTopLevelTokens,
          inventory.excludedInlineEvidence)) {
        unexpected.push(value);
      } else excludedCount++;
    }
    excludedNativeCounts[name] = excludedCount;
    if (missing.length || unexpected.length || semanticDifferences.length) {
      mismatches.push({
        projection: name,
        classification: "requires-human-classification-not-automatically-a-renderer-defect",
        markedCandidateCount: markedValues.length,
        missingNativeCount: missing.length,
        unexpectedNativeCount: unexpected.length,
        semanticDifferenceCount: semanticDifferences.length,
        nativeExcludedFromSubsetCount: excludedCount,
        sourceSpanConventionDifferenceCount: conventions.length,
        firstDifference: {
          missingMarkedCandidate: missing[0] ?? null,
          unexpectedNativeProjection: unexpected[0] ?? null,
          semanticDifference: semanticDifferences[0] ?? null,
        },
      });
    }
  }
  const hasMismatch = mismatches.length !== 0;
  const hasCandidates = candidateElementCount !== 0;
  return {
    status: hasMismatch
      ? "mismatch-requires-classification"
      : !hasCandidates
        ? "not-compared-no-common-subset-elements"
        : spanConventionDifferenceCount > 0
          ? "matched-common-subset-with-source-span-conventions"
          : "matched-common-subset",
    mismatches,
    excludedNativeProjectionCounts: excludedNativeCounts,
    candidateElementCount,
    comparedElementCount,
    sourceSpanConventionDifferenceCount: spanConventionDifferenceCount,
    sourceSpanConventionDifferences: spanConventionDifferences,
    omittedSourceSpanConventionDetailCount: Math.max(0, spanConventionDifferenceCount - spanConventionDifferences.length),
  };
}

function isNativeCommonSubsetEntry(name, entry, source, topLevelEvidence, excludedInlineEvidence) {
  if (!hasValidProjectionSpan(entry, source)) return false;
  const raw = source.slice(entry.sourceStart, entry.sourceStart + entry.sourceLength);
  if (name === "headings") {
    return isWithinEvidenceScope(entry, topLevelEvidence,
      evidence => evidence.tokenType === "heading" && evidence.classification === "projected-common-subset") &&
      /^#{1,6} ([^\r\n#][^\r\n]*)$/u.test(raw) && !containsGithubTextExtension(raw);
  }
  if (name === "codeBlocks") {
    const match = /^\x60{3}([A-Za-z0-9_.+-]*)\r?\n([\s\S]*?)\r?\n\x60{3}\r?\n?$/u.exec(raw);
    return Boolean(match) && entry.language?.toLowerCase() !== "mermaid" &&
      isWithinEvidenceScope(entry, topLevelEvidence,
        evidence => evidence.tokenType === "code" && evidence.classification === "projected-common-subset");
  }
  const link = /^\[([\p{L}\p{N} ._-]+)\]\(([^()\s<>\\]+)\)$/u.test(raw);
  const image = /^!\[([\p{L}\p{N} ._-]+)\]\(([^()\s<>\\]+)\)$/u.test(raw);
  if (name === "links" || name === "images") {
    const inPlainParagraph = isWithinEvidenceScope(entry, topLevelEvidence,
      container => container.tokenType === "paragraph" &&
        container.classification === "container-for-inline-projections");
    const excludedInlineContext = Array.isArray(excludedInlineEvidence) && excludedInlineEvidence.some(item =>
      Number.isSafeInteger(item.sourceStart) && Number.isSafeInteger(item.sourceLength) &&
      item.sourceStart <= entry.sourceStart &&
      item.sourceStart + item.sourceLength >= entry.sourceStart + entry.sourceLength);
    return inPlainParagraph && !excludedInlineContext && (name === "links" ? link : image);
  }
  return false;
}

function hasValidProjectionSpan(entry, source) {
  return Number.isSafeInteger(entry?.sourceStart) && Number.isSafeInteger(entry?.sourceLength) &&
    entry.sourceStart >= 0 && entry.sourceLength > 0 && entry.sourceStart + entry.sourceLength <= source.length;
}

function isWithinEvidenceScope(entry, evidence, predicate) {
  return Array.isArray(evidence) && evidence.some(container =>
    predicate(container) && Number.isSafeInteger(container.sourceStart) &&
    Number.isSafeInteger(container.sourceLength) && container.sourceStart >= 0 && container.sourceLength >= 0 &&
    entry.sourceStart >= container.sourceStart &&
    entry.sourceStart + entry.sourceLength <= container.sourceStart + container.sourceLength);
}

function projectionSpanKey(value) {
  if (!Number.isSafeInteger(value?.sourceStart) || !Number.isSafeInteger(value?.sourceLength)) return null;
  return value.sourceStart + ":" + value.sourceLength;
}

function projectionSemantics(name, value) {
  const { sourceStart: _sourceStart, sourceLength: _sourceLength, ...semantics } = value;
  return semantics;
}

function findCodeSpanConvention(marked, nativeValues, matchedNative, source) {
  if (!Number.isSafeInteger(marked?.sourceStart) || !Number.isSafeInteger(marked?.sourceLength)) return null;
  for (const native of nativeValues) {
    if (matchedNative.has(native) || native?.sourceStart !== marked.sourceStart ||
        !Number.isSafeInteger(native?.sourceLength) || native.sourceLength <= 0 ||
        native.sourceLength >= marked.sourceLength) continue;
    const suffix = source.slice(marked.sourceStart + native.sourceLength,
      marked.sourceStart + marked.sourceLength);
    if ((suffix === "\n" || suffix === "\r\n") && native.sourceLength + suffix.length === marked.sourceLength &&
        source.slice(marked.sourceStart, marked.sourceStart + native.sourceLength).endsWith("```")) {
      return { native, lineTerminator: suffix };
    }
  }
  return null;
}

function canonicalJson(value) {
  if (Array.isArray(value)) return "[" + value.map(canonicalJson).join(",") + "]";
  if (value && typeof value === "object") {
    return "{" + Object.keys(value).sort().map(key =>
      JSON.stringify(key) + ":" + canonicalJson(value[key])).join(",") + "}";
  }
  return JSON.stringify(value);
}

function coverage(source, joinedRaw, exact) {
  return {
    status: exact ? "exact" : "not-exact",
    sourceUtf16Length: source.length,
    concatenatedTopLevelRawUtf16Length: joinedRaw.length,
    sourceSha256: sha256(Buffer.from(source, "utf8")),
  };
}

function children(token) {
  const result = [];
  if (Array.isArray(token.tokens)) result.push(...token.tokens);
  if (token.type === "table") {
    if (Array.isArray(token.header)) result.push(...token.header);
    if (Array.isArray(token.rows)) {
      for (const row of token.rows) if (Array.isArray(row)) result.push(...row);
    }
  }
  if (Array.isArray(token.items)) {
    for (const item of token.items) {
      if (Array.isArray(item?.tokens)) result.push(...item.tokens);
      if (Array.isArray(item?.items)) result.push(...item.items);
    }
  }
  return result;
}

function normalizeCodeText(value) {
  return value.replace(/\r\n/gu, "\n").replace(/\n$/u, "");
}

export function validateFrozenManifest(manifest) {
  if (manifest?.schemaVersion !== 2 || manifest.requestedCount !== EXPECTED_COUNT ||
      !Array.isArray(manifest.repositories) || manifest.repositories.length !== EXPECTED_COUNT) {
    throw new Error("Expected the pinned schema-v2 top-500 manifest.");
  }
  const ids = new Set();
  const names = new Set();
  for (let index = 0; index < manifest.repositories.length; index++) {
    const repository = manifest.repositories[index];
    const readme = repository?.readme;
    if (repository?.rank !== index + 1 || !Number.isSafeInteger(repository.id) ||
        repository.id <= 0 || ids.has(repository.id) || typeof repository.fullName !== "string" ||
        !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u.test(repository.fullName) ||
        names.has(repository.fullName.toLowerCase()) || !HEX_40.test(repository.commitSha ?? "") ||
        readme?.available !== true || !Number.isSafeInteger(readme.byteSize) ||
        readme.byteSize < 0 || readme.byteSize > MAX_README_BYTES || !HEX_40.test(readme.sha ?? "") ||
        typeof readme.path !== "string" || readme.path.startsWith("/") || readme.path.includes("\\") ||
        readme.path.split("/").some(segment => segment === "" || segment === "." || segment === "..")) {
      throw new Error("Invalid frozen repository or README identity at rank " + (index + 1) + ".");
    }
    ids.add(repository.id);
    names.add(repository.fullName.toLowerCase());
  }
  return true;
}

function validateNativeCases(cases, manifest) {
  if (cases.length !== EXPECTED_COUNT) throw new Error("Native cases must contain exactly 500 rows.");
  const byRank = new Map();
  for (const item of cases) {
    const repository = manifest.repositories[item?.rank - 1];
    if (!repository || byRank.has(item.rank) || item.rank !== repository.rank ||
        item.repositoryId !== repository.id || item.fullName !== repository.fullName ||
        item.commitSha?.toLowerCase() !== repository.commitSha.toLowerCase() ||
        item.readmePath !== repository.readme.path ||
        item.expectedGitBlobSha1?.toLowerCase() !== repository.readme.sha.toLowerCase() ||
        item.observedGitBlobSha1?.toLowerCase() !== repository.readme.sha.toLowerCase() ||
        item.expectedByteSize !== repository.readme.byteSize ||
        item.observedByteSize !== repository.readme.byteSize ||
        !HEX_64.test(item.rawSourceSha256 ?? "")) {
      throw new Error("Native case identity mismatch at rank " + String(item?.rank) + ".");
    }
    byRank.set(item.rank, item);
  }
  return byRank;
}

function parseCases(bytes) {
  const text = decodeUtf8(bytes, "native cases");
  return text.split(/\r?\n/u).filter(Boolean).map((line, index) => {
    try {
      return JSON.parse(line);
    } catch {
      throw new Error("Invalid native JSONL at line " + (index + 1) + ".");
    }
  });
}

function parseJson(bytes, label) {
  const text = decodeUtf8(bytes, label);
  try { return JSON.parse(text); }
  catch { throw new Error(label + " is not valid JSON."); }
}

async function readReadmeObject(objectRoot, repository) {
  const sha = repository.readme.sha.toLowerCase();
  const file = path.join(objectRoot, "git-blob-sha1", sha.slice(0, 2), sha + ".md");
  const bytes = await readBoundedFile(file, MAX_README_BYTES, repository.readme.byteSize);
  verifyFrozenReadmeBytes(bytes, repository.readme.byteSize, sha);
  return bytes;
}

export function verifyFrozenReadmeBytes(bytes, expectedByteSize, expectedGitBlobSha1) {
  if (!Buffer.isBuffer(bytes) || !Number.isSafeInteger(expectedByteSize) ||
      expectedByteSize < 0 || expectedByteSize > MAX_README_BYTES ||
      bytes.length !== expectedByteSize || !HEX_40.test(expectedGitBlobSha1 ?? "") ||
      gitBlobSha1(bytes) !== expectedGitBlobSha1.toLowerCase()) {
    throw new Error("Frozen README bytes do not match the declared size and Git blob SHA-1.");
  }
  return true;
}

async function resolveObjectRoot(input) {
  const info = await lstat(input);
  if (!info.isDirectory() || info.isSymbolicLink()) throw new Error("Object root must be a real directory.");
  if (await realDirectory(path.join(input, "git-blob-sha1"))) return input;
  const objects = path.join(input, "objects");
  if (await realDirectory(path.join(objects, "git-blob-sha1"))) return objects;
  throw new Error("Object root does not contain git-blob-sha1.");
}

async function realDirectory(file) {
  try {
    const info = await lstat(file);
    return info.isDirectory() && !info.isSymbolicLink();
  } catch {
    return false;
  }
}

async function readBoundedFile(file, maximum, expectedLength = null) {
  const before = await lstat(file);
  if (!before.isFile() || before.isSymbolicLink() || before.size > maximum ||
      (expectedLength != null && before.size !== expectedLength)) {
    throw new Error("Input is not a bounded regular file with declared length: " + file);
  }
  const handle = await open(file, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
  try {
    const opened = await handle.stat();
    if (!opened.isFile() || opened.size > maximum ||
        (expectedLength != null && opened.size !== expectedLength) ||
        (before.ino !== 0 && opened.ino !== before.ino)) {
      throw new Error("Input changed during bounded open: " + file);
    }
    const length = expectedLength ?? opened.size;
    const buffer = Buffer.alloc(length + 1);
    let offset = 0;
    while (offset < buffer.length) {
      const { bytesRead } = await handle.read(buffer, offset, buffer.length - offset, null);
      if (bytesRead === 0) break;
      offset += bytesRead;
    }
    if (offset !== length) throw new Error("Input changed during bounded read: " + file);
    return buffer.subarray(0, offset);
  } finally {
    await handle.close();
  }
}

function decodeUtf8(bytes, label) {
  try {
    // Preserve U+FEFF to match .NET Encoding.UTF8.GetString; frozen inputs
    // currently have no BOM, and invalid UTF-8 is never silently replaced.
    return new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  } catch {
    throw new Error(label + " is not valid UTF-8.");
  }
}

function absolutePath(value, label) {
  if (typeof value !== "string" || value.trim().length === 0) throw new Error(label + " is required.");
  return path.resolve(value);
}

function assertDisjoint(output, input, label) {
  const outputToInput = path.relative(output, input);
  const inputToOutput = path.relative(input, output);
  const within = value => value === "" ||
    (!path.isAbsolute(value) && value !== ".." && !value.startsWith(".." + path.sep));
  if (within(outputToInput) || within(inputToOutput)) throw new Error("Output must not overlap " + label + ".");
}

async function exists(file) {
  try { await lstat(file); return true; }
  catch (error) { if (error?.code === "ENOENT") return false; throw error; }
}

async function writeExclusive(file, bytes) {
  const handle = await open(file, "wx");
  try { await handle.writeFile(bytes); await handle.sync(); }
  finally { await handle.close(); }
}

function gitBlobSha1(bytes) {
  return createHash("sha1")
    .update(Buffer.from("blob " + bytes.length + "\0", "ascii"))
    .update(bytes)
    .digest("hex");
}

function sha256(bytes) { return createHash("sha256").update(bytes).digest("hex"); }
function hashText(value) { return sha256(Buffer.from(value, "utf8")); }

async function main(args) {
  if (args.length !== 4 || args[0] === "--help" || args[0] === "-h") {
    process.stderr.write(
      "Usage: node eng/readme-audit/correctness-source-inventory.mjs " +
      "<frozen-manifest.json> <source-acquisition-root-or-objects> <native-cases.jsonl> <fresh-output-directory>\n",
    );
    return 2;
  }
  const result = await createCorrectnessSourceInventory({
    manifestPath: args[0],
    sourceObjectRoot: args[1],
    nativeCasesPath: args[2],
    outputDirectory: args[3],
  });
  process.stdout.write(
    "Source inventory completed (diagnostic only): cases=" + result.summary.manifestRepositoryCount +
    "; matched=" + result.summary.matchedCommonSubsetCaseCount +
    "; matched-with-source-span-conventions=" + result.summary.matchedWithSourceSpanConventionCaseCount +
    "; mismatches-requiring-classification=" + result.summary.mismatchRequiresClassificationCaseCount +
    "; not-compared-empty-common-subset=" + result.summary.notComparedNoCommonSubsetCaseCount +
    "; compared-elements=" + result.summary.comparedElementCount +
    "; coverage-failures=" + result.summary.sourceTokenCoverageFailureCount +
    "; output=" + result.outputDirectory + "\n",
  );
  return 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2)).then(code => { process.exitCode = code; }).catch(error => {
    process.stderr.write("Source inventory failed: " + (error instanceof Error ? error.message : "unknown error") + "\n");
    process.exitCode = 2;
  });
}
