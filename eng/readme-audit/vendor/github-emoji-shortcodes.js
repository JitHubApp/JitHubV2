/*
 * Minimal offline GFM shortcode adapter for the same-byte audit parser.
 *
 * The data is generated from the separately pinned github/gemoji dataset.
 * This adapter intentionally rewrites Marked text tokens only: code spans,
 * fenced code, raw HTML, link destinations, and image destinations stay literal.
 */
(() => {
  "use strict";

  const aliasPattern = /^[a-z0-9_+-]{1,64}$/u;
  const shortcodePattern = /:([a-z0-9_+-]{1,64}):/gu;
  const scalarValuePattern = /^[^\u0000-\u001f\u007f-\u009f<>]{1,64}$/u;

  Object.defineProperty(window, "installPinnedGitHubEmojiShortcodes", {
    configurable: false,
    enumerable: false,
    writable: false,
    value(marked, sourceMap, expectedAliasCount) {
      if (!marked || typeof marked.use !== "function" ||
          !sourceMap || typeof sourceMap !== "object" || Array.isArray(sourceMap) ||
          !Number.isSafeInteger(expectedAliasCount) || expectedAliasCount <= 0 ||
          Object.keys(sourceMap).length !== expectedAliasCount) {
        throw new TypeError("Pinned GitHub emoji shortcode input is invalid.");
      }

      const aliases = Object.create(null);
      for (const [alias, value] of Object.entries(sourceMap)) {
        if (!aliasPattern.test(alias) || typeof value !== "string" ||
            !scalarValuePattern.test(value) || Array.from(value).length > 32 ||
            /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(value)) {
          throw new TypeError("Pinned GitHub emoji shortcode map contains an invalid entry.");
        }
        aliases[alias] = value;
      }

      marked.use({
        walkTokens(token) {
          if (!token || token.type !== "text" || typeof token.text !== "string") return;
          token.text = token.text.replace(shortcodePattern, (source, alias) =>
            Object.hasOwn(aliases, alias) ? aliases[alias] : source);
        },
      });
      return true;
    },
  });
})();
