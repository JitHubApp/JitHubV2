# MarkdownRenderer.All

This is the deliberately large, explicit opt-in package for applications that
want every MarkdownRenderer feature pack, the isolated resvg SVG workers, and the
complete TextMate grammar resources.

This meta-package includes the current preview implementations, including the
managed Math processor, selected-RID Mermaid native engine, and optional
progressive performance session. It does not make the session a default for
the lean package or turn cross-built payload checks into physical-device or
full release certification.

Most applications should reference `MarkdownRenderer` plus only the feature
packs they use. The lean convenience package never pulls these optional payloads
into an application by default.
