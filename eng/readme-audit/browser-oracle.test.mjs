import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { pruneClosedDetailsReplayContent } from "./closed-details-snapshot.mjs";
import { readRenderedSnapshotExpression } from "./snapshot-expression.test-support.mjs";
import {
  hasUncapturedExternalInlineSvgResource,
  isSourceLessDecorativeImageSpacerCandidate,
} from "./same-byte-corpus.mjs";

const readmeAuditDirectory = path.dirname(fileURLToPath(import.meta.url));

function element(tagName, attributes = [], children = [], namespaceURI = "http://www.w3.org/1999/xhtml") {
  const node = {
    nodeType: 1,
    tagName: tagName.toUpperCase(),
    namespaceURI,
    attributes: new Set(attributes),
    childNodes: [],
    get children() {
      return this.childNodes.filter(child => child.nodeType === 1);
    },
    hasAttribute(name) {
      return this.attributes.has(name);
    },
    append(child) {
      if (child.parentNode) child.remove();
      child.parentNode = this;
      this.childNodes.push(child);
      return child;
    },
    remove() {
      if (!this.parentNode) return;
      const siblings = this.parentNode.childNodes;
      const index = siblings.indexOf(this);
      if (index >= 0) siblings.splice(index, 1);
      this.parentNode = null;
    },
    querySelectorAll(selector) {
      const expectedTag = selector.toUpperCase();
      const matches = [];
      const visit = parent => {
        for (const child of parent.childNodes) {
          if (child.nodeType !== 1) continue;
          if (child.tagName === expectedTag) matches.push(child);
          visit(child);
        }
      };
      visit(this);
      return matches;
    },
  };
  for (const child of children) node.append(child);
  return node;
}

function text(value) {
  return {
    nodeType: 3,
    nodeValue: value,
    parentNode: null,
    remove() {
      if (!this.parentNode) return;
      const siblings = this.parentNode.childNodes;
      const index = siblings.indexOf(this);
      if (index >= 0) siblings.splice(index, 1);
      this.parentNode = null;
    },
  };
}

function descendants(root, tagName) {
  return root.querySelectorAll(tagName);
}

test("closed details replay pruning preserves the summary and removes hidden resources", () => {
  const whitespaceBefore = text("\n  ");
  const whitespaceAfter = text("\n");
  const summaryText = text("Examples ");
  const summaryLink = element("a", [], [text("open later")]);
  const summaryImage = element("img");
  const summary = element("summary", [], [summaryText, summaryLink, summaryImage]);
  const hiddenImage = element("img");
  const hiddenContent = element("div", [], [text("not visible"), hiddenImage]);
  const details = element("details", [], [
    whitespaceBefore,
    summary,
    whitespaceAfter,
    hiddenContent,
    element("summary", [], [text("second summary is not the disclosure label")]),
  ]);
  const visibleSibling = element("p", [], [text("visible outside details")]);
  const article = element("article", [], [details, visibleSibling]);

  pruneClosedDetailsReplayContent(article);

  assert.deepEqual(details.childNodes, [whitespaceBefore, summary, whitespaceAfter]);
  assert.equal(summary.parentNode, details);
  assert.equal(summaryLink.parentNode, summary);
  assert.equal(summaryImage.parentNode, summary,
    "resources in the first direct summary remain visible and available");
  assert.equal(hiddenContent.parentNode, null);
  assert.equal(visibleSibling.parentNode, article);
  assert.deepEqual(descendants(article, "img"), [summaryImage],
    "only images hidden outside the first direct summary are removed from replay");
});

test("open details stay intact while nested closed details are pruned", () => {
  const nestedSummary = element("summary", [], [text("nested summary")]);
  const nestedImage = element("img");
  const nested = element("details", [], [nestedSummary, element("div", [], [nestedImage])]);
  const openImage = element("img");
  const openSummary = element("summary", [], [text("open disclosure")]);
  const open = element("details", ["open"], [openSummary, openImage, nested]);
  const article = element("article", [], [open]);

  pruneClosedDetailsReplayContent(article);

  assert.deepEqual(open.childNodes, [openSummary, openImage, nested]);
  assert.deepEqual(nested.childNodes, [nestedSummary]);
  assert.deepEqual(descendants(article, "img"), [openImage]);
});

test("a closed outer disclosure prunes its entire subtree without changing open siblings", () => {
  const outerSummary = element("summary", [], [text("outer")]);
  const nestedOpen = element("details", ["open"], [
    element("summary", [], [text("nested open")]),
    element("img"),
  ]);
  const closed = element("details", [], [outerSummary, nestedOpen]);
  const openSibling = element("details", ["open"], [
    element("summary", [], [text("sibling")]),
    element("img"),
  ]);
  const article = element("article", [], [closed, openSibling]);

  pruneClosedDetailsReplayContent(article);

  assert.deepEqual(closed.childNodes, [outerSummary]);
  assert.deepEqual(openSibling.childNodes[1], descendants(openSibling, "img")[0]);
  assert.equal(descendants(article, "img").length, 1);
});

test("closed details without a summary remove hidden text and images but keep whitespace and SVG details", () => {
  const whitespace = text("\n  ");
  const hiddenImage = element("img");
  const htmlDetails = element("details", [], [whitespace, text("hidden without summary"), hiddenImage]);
  const svgImage = element("img", [], [], "http://www.w3.org/2000/svg");
  const svgSummary = element("summary", [], [text("SVG child")], "http://www.w3.org/2000/svg");
  const svgDetails = element("details", [], [svgSummary, svgImage],
    "http://www.w3.org/2000/svg");
  const article = element("article", [], [htmlDetails, svgDetails]);

  pruneClosedDetailsReplayContent(article);

  assert.deepEqual(htmlDetails.childNodes, [whitespace]);
  assert.deepEqual(svgDetails.childNodes, [svgSummary, svgImage],
    "the HTML disclosure snapshot rule must not rewrite same-named SVG elements");
  assert.equal(svgImage.parentNode, svgDetails);
  assert.deepEqual(descendants(article, "img"), [svgImage]);
});

test("only small, explicitly decorative, resource-free images qualify as layout spacers", () => {
  const spacer = {
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
  };

  assert.equal(isSourceLessDecorativeImageSpacerCandidate(spacer), true);
  for (const mutation of [
    { hasExplicitAlt: false },
    { alt: "meaningful" },
    { hasSrcAttribute: true },
    { hasSrcsetAttribute: true },
    { currentSource: "https://example.invalid/image.png" },
    { hasCanonicalSourceAttribute: true },
    { hasDataSourceAttribute: true },
    { hasDataSrcsetAttribute: true },
    { hasDataLazySourceAttribute: true },
    { pictureHasSourceResource: true },
    { width: 65 },
    { renderedHeight: 0 },
    { renderedWidth: 65 },
    { width: Number.NaN },
  ]) {
    assert.equal(isSourceLessDecorativeImageSpacerCandidate({ ...spacer, ...mutation }), false,
      `candidate mutation ${JSON.stringify(mutation)} must not qualify`);
  }
});

test("external inline SVG references are rejected before snapshot sanitization", () => {
  const svg = "http://www.w3.org/2000/svg";
  for (const localName of ["image", "IMAGE", "Use", "feImage"]) {
    assert.equal(hasUncapturedExternalInlineSvgResource({
      namespaceURI: svg,
      localName,
      href: "https://cdn.example.invalid/resource.svg#item",
    }), true, `${localName} external href must not be silently removed from replay`);
  }
  assert.equal(hasUncapturedExternalInlineSvgResource({
    namespaceURI: svg,
    localName: "use",
    xlinkHref: "https://cdn.example.invalid/symbols.svg#icon",
  }), true, "legacy xlink:href external resources must also be rejected");
  assert.equal(hasUncapturedExternalInlineSvgResource({
    namespaceURI: svg,
    localName: "use",
    href: "#local-symbol",
  }), false, "same-document SVG references remain supported");
  assert.equal(hasUncapturedExternalInlineSvgResource({
    namespaceURI: svg,
    localName: "image",
    href: "data:image/png;base64,AAAA",
  }), false, "already-embedded image data remains supported");
  assert.equal(hasUncapturedExternalInlineSvgResource({
    namespaceURI: svg,
    localName: "a",
    href: "https://example.invalid/read-more",
  }), false, "ordinary SVG links remain links rather than resource fetches");
  assert.equal(hasUncapturedExternalInlineSvgResource({
    namespaceURI: "http://www.w3.org/1999/xhtml",
    localName: "image",
    href: "https://example.invalid/not-svg",
  }), false, "a same-named non-SVG element is outside this SVG-specific rule");
});

test("isolated snapshot expression validates SVG resources without host-module globals", async () => {
  const expression = await readRenderedSnapshotExpression();
  const capture = new Function("document", "Node", "getComputedStyle", `return (${expression});`);
  function captureFixture(resource = null) {
    const makeNode = (localName, namespaceURI, href = null, xlinkHref = null) => ({
      nodeType: 1,
      tagName: namespaceURI === "http://www.w3.org/2000/svg" ? localName : localName.toUpperCase(),
      localName,
      namespaceURI,
      attributes: [],
      style: { setProperty() {} },
      getAttribute(name) { return name === "href" ? href : null; },
      getAttributeNS(namespace, name) {
        assert.equal(namespace, "http://www.w3.org/1999/xlink");
        assert.equal(name, "href");
        return xlinkHref;
      },
      querySelectorAll() { return []; },
    });
    const article = makeNode("article", "http://www.w3.org/1999/xhtml");
    const clone = makeNode("article", "http://www.w3.org/1999/xhtml");
    const children = resource ? [makeNode("use", "http://www.w3.org/2000/svg", resource.href, resource.xlinkHref)] : [];
    article.querySelectorAll = selector => selector === "*" ? children : [];
    clone.querySelectorAll = selector => selector === "*" ? children.map(() => makeNode("use", "http://www.w3.org/2000/svg")) : [];
    clone.outerHTML = "<article></article>";
    article.cloneNode = () => clone;
    return capture({ querySelector: () => article }, { ELEMENT_NODE: 1 }, () => ({ getPropertyValue: () => "" }));
  }

  assert.equal(captureFixture().html, "<article></article>", "ordinary capture must not depend on a host-only namespace constant");
  assert.doesNotThrow(() => captureFixture({ href: "#local-symbol" }));
  assert.throws(() => captureFixture({ href: "https://example.invalid/symbols.svg#icon" }),
    /uncaptured external inline-SVG resource/u);
  assert.throws(() => captureFixture({ xlinkHref: "https://example.invalid/symbols.svg#icon" }),
    /uncaptured external inline-SVG resource/u);
});

test("capture validates original resource-bearing nodes before pruning and stages the helper", async () => {
  const browserOracle = await readFile(path.join(readmeAuditDirectory, "browser-oracle.mjs"), "utf8");
  const sameByteCorpus = await readFile(path.join(readmeAuditDirectory, "same-byte-corpus.mjs"), "utf8");
  const edgeReplay = await readFile(path.join(readmeAuditDirectory, "same-byte-edge-replay.mjs"), "utf8");
  const publicCapture = await readFile(
    path.resolve(readmeAuditDirectory, "../Invoke-TopReadmeSandboxPublicSameByteCapture.ps1"), "utf8");
  const offlineReplay = await readFile(
    path.resolve(readmeAuditDirectory, "../Invoke-TopReadmeSandboxOfflineSameByteReplay.ps1"), "utf8");

  assert.match(browserOracle, /const pruneClosedDetailsReplayContent = \$\{pruneClosedDetailsReplayContent\.toString\(\)\};/u);
  assert.match(browserOracle,
    /const isSourceLessDecorativeImageSpacerCandidate = \$\{isSourceLessDecorativeImageSpacerCandidate\.toString\(\)\};/u);
  assert.match(browserOracle,
    /const hasUncapturedExternalInlineSvgResource = \$\{hasUncapturedExternalInlineSvgResource\.toString\(\)\};/u,
    "the pure SVG resource predicate is available inside the isolated capture expression");
  assert.match(browserOracle, /data-jithub-\(\?:image\|layout-spacer\|hidden-image\)/u,
    "page-provided capture markers must be removed before the harness generates its own identities");
  assert.match(sameByteCorpus,
    /sourceLessDecorativeImageSpacers: layoutImageSpacers/u,
    "the spacer identity is bound into the captured corpus manifest");
  assert.match(browserOracle,
    /cssHiddenImageOmissions: renderedHtml\?\.cssHiddenImageOmissions \|\| \[\]/u,
    "the CSS-hidden inventory is carried into its separate corpus manifest field");
  assert.match(sameByteCorpus,
    /cssHiddenImageOmissions: hiddenImageOmissions/u,
    "CSS-hidden omissions are not folded into ordinary route exclusions");
  const validateExternalCss = browserOracle.indexOf("if (hasExternalCssResource(computed.getPropertyValue(property)))");
  const validateExternalSvg = browserOracle.indexOf("GitHub article contains an uncaptured external inline-SVG resource.");
  const validateExcludedInventory = browserOracle.indexOf(
    "if (observedExcludedDecorativeImageIndexes.size !== excludedDecorativeImageIndexes.size)");
  const prune = browserOracle.indexOf("pruneClosedDetailsReplayContent(clone)");
  const removeActiveNodes = browserOracle.indexOf('clone.querySelectorAll("script,iframe,object,embed,base,link[rel~=\'stylesheet\'],style")');
  assert.ok(validateExternalCss >= 0 && validateExternalCss < prune);
  assert.ok(validateExternalSvg >= 0 && validateExternalSvg < prune);
  assert.ok(validateExcludedInventory >= 0 && validateExcludedInventory < prune);
  assert.ok(prune < removeActiveNodes);

  assert.match(edgeReplay, /const route = routeByIndex\.get\(index\);\s*if \(!route\) \{/u,
    "ordinary images without captured routes must remain un-routed");
  assert.match(edgeReplay,
    /if \(failureIndex >= 0\) \{\s*throw new Error\("A visible offline image did not decode successfully/u,
    "a visible image with no captured route must still fail replay");

  assert.match(publicCapture, /eng\\readme-audit\\closed-details-snapshot\.mjs/u);
  assert.match(offlineReplay, /eng\\readme-audit\\closed-details-snapshot\.mjs/u);
});
