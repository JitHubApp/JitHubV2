# MarkdownRenderer conformance harness

This tool runs every official CommonMark 0.31.2 and GFM 0.29 example through
the built `MarkdownRenderer.Core` assembly. It verifies pinned corpus SHA-256
digests, exact source retention, half-open UTF-16 source ranges, immutable
semantic invariants, byte-stable semantic-plan serialization, and exact official
reference-HTML parity.

The comparison-only oracle allows raw HTML passthrough because the specifications
require it. Production `CommonMark` and `GfmStrict` profiles retain the locked
security policy of rendering raw HTML literally; separate tests gate that behavior.
A `PASS` requires both official reference-HTML parity and deterministic semantic
plan integrity.

Run both suites (downloads the pinned corpora on first use):

```powershell
dotnet run --project MarkdownRenderer/MarkdownRenderer.Conformance/MarkdownRenderer.Conformance.csproj -c Release -- --suite all
```

Use `--offline` in a hermetic CI phase after caching. CommonMark publishes
`spec.json` directly. The pinned GFM repository publishes annotated `spec.txt`;
the harness verifies those official bytes and writes a normalized `spec.json`
beside the cached source.

Expected differences belong in `conformance-exceptions.v1.json`. Every entry
must identify one suite/example, explain the difference, and link an HTTPS issue.
An allowlisted example that starts passing is reported as a stale-exception
failure so the list cannot silently grow obsolete.
