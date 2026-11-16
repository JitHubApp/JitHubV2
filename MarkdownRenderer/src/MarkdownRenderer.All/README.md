# MarkdownRenderer.All

This is the deliberately large, explicit opt-in package for applications that
want every MarkdownRenderer feature pack, the isolated resvg SVG workers, and the
complete TextMate grammar resources.

This meta-package depends on `MarkdownRenderer` and every feature pack in its
current preview implementation: the managed Math processor, the selected-RID
Mermaid native engine, the isolated resvg SVG provider, TextMate integration,
and the complete grammar corpus. It does not turn cross-built payload checks
into physical-device or full release certification.

Most applications should reference `MarkdownRenderer` plus only the feature
packs they use. `MarkdownRenderer` never pulls these optional payloads into an
application by default.
