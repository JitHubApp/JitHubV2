/**
 * Removes hidden branches of closed disclosure widgets from a static replay
 * snapshot. The source page remains untouched. Computed styles are inlined on
 * descendants during capture, so relying on the browser's user-agent closed-
 * details rule would otherwise expose hidden content in the replay document.
 */
export function pruneClosedDetailsReplayContent(root) {
  for (const details of root.querySelectorAll("details")) {
    if (details.namespaceURI !== "http://www.w3.org/1999/xhtml" || details.hasAttribute("open")) continue;

    let summary = null;
    for (const child of details.children) {
      if (child.tagName === "SUMMARY") {
        summary = child;
        break;
      }
    }

    // Walk backwards because childNodes is live. Keep the first summary and
    // whitespace-only text nodes; the latter are harmless and stable across
    // serialization. Everything else is non-rendered content in the captured
    // closed state and must not become visible after computed styles are
    // copied inline.
    for (let index = details.childNodes.length - 1; index >= 0; index--) {
      const child = details.childNodes[index];
      if (child === summary) continue;
      if (child.nodeType === 3 && /^\s*$/u.test(child.nodeValue || "")) continue;
      child.remove();
    }
  }
}
