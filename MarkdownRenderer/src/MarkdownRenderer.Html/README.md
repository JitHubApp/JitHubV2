# MarkdownRenderer.Html

This package contains the native safe-HTML parser and Win2D painter, immutable options,
audited default resource budgets, and a stable capability descriptor.

The capability contract is deliberately unable to enable script, CSS layout, a browser
DOM, filesystem access, or direct network access. Links and images are always declared
as host-mediated resources. Use `UseSafeHtml` with an engine profile that preserves
HTML nodes; the `MarkdownRenderer.GitHub` package does this automatically for its
`GitHubReadme` profile. CommonMark and strict GFM continue to render raw HTML literally.
The `MarkdownEngineBuilder.UseSafeHtml(options)` overload freezes the policy with the
engine; view/control overloads create an immutable view-specific override when needed.

Configured budgets apply to HTML blocks, inline tags and cross-block scopes.
Inline node/input accounting starts at the first HTML tag in the inline
container; cross-block tag accounting is shared for the document's top-level
HTML scopes. Nesting counts actual elements, not a synthetic document root.
Over-budget content emits a localized omission notice. These rendering limits
do not replace the engine's overall Markdown source and parsing limits.
