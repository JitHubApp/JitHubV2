# MarkdownRenderer package compliance tooling

Run all package checks against a fresh package output directory:

```powershell
./Invoke-PackageCompliance.ps1 `
    -PackageDirectory ../artifacts/packages `
    -OutputDirectory ../artifacts/package-compliance
```

For a release gate, add `-FailOnUnknownLicense`. The ordinary mode still writes all evidence but records unknown package or dependency licenses as `NOASSERTION` in the SPDX document, notices report, and summary. It never infers a license from a package name. Produced sibling packages are resolved by exact package ID and version, and their dependency graphs are traversed, so direct and transitive declarations and embedded notice assets can be carried into each package report.

Every invocation also applies `license-compatibility-policy.json` to the generated NuGet SPDX closure and the vendored/native Math, Mermaid, resvg, and TextMate inventories. A known license that is absent from the reviewed allowlist fails just like `NOASSERTION`; known metadata is not treated as compatibility approval. The four reviewed MPL-2.0 Mermaid crates remain an exact pinned set and require the packaged source-availability notice. The resvg worker selects the upstream MIT option and its complete locked Rust closure must have reviewed, compatible licenses. Any unknown or changed dependency fails closed. The optional EPL-licensed ELK Mermaid layout must remain excluded.

The output includes one SPDX 2.3 JSON SBOM and one notices-evidence report per nupkg, a summary of license findings, `license-compatibility.json`, a selected-RID native/ABI report, and a deterministic package manifest containing package and archive-entry hashes and sizes. Supply a manifest from an independent build through `-ReferenceManifest` to require byte-for-byte package reproducibility.

`Test-NativePackageCompliance.ps1` verifies each native DLL or executable's PE machine against its `win-x86`, `win-x64`, or `win-arm64` path. Native libraries must have an exact exported-symbol allowlist; isolated worker executables must declare an empty export surface. Use `-InventoryOnly` to review a new native payload without weakening the normal gate; update the checked-in allowlist only after reviewing the ABI change. A package configured with `allowNoNative` is reported explicitly when no native payload exists.

The complete SVG provider evidence runner has two explicit modes:

```powershell
# Developer/PR evidence; unsigned workers are reported but do not claim release readiness.
./Invoke-SvgResvgReleaseEvidence.ps1 -Mode Preview

# Production evidence; Edge and trusted Authenticode signatures are mandatory.
./Invoke-SvgResvgReleaseEvidence.ps1 -Mode Release `
    -EdgePerformanceEvidence <same-machine-edge.json> `
    -DeviceMatrixEvidence <device-theme-dpi.json> `
    -FaultInjectionEvidence <worker-faults.json> `
    -StoreDeploymentEvidence <signed-store-deployment.json> `
    -LiveAuditEvidence <representative-repositories.json>
```

Both modes run provider/fault and pixel tests, package and RID/PE inspection, reproducibility and license/SBOM checks, x86/x64/ARM64 NativeAOT plus trim publishes, and missing/wrong-worker fallback probes. Release mode additionally fails closed when Edge is unavailable, any packaged worker is not trusted-signed, or one of the five external machine-readable evidence files is absent or failing. Those inputs preserve the distinction between what this orchestrator ran and pinned-machine hardware/performance, full injected-fault, Store child-process, and representative live-repository evidence; preview mode records missing inputs as `not-provided-preview` and never claims release readiness.

Each external input is schema version 1, has the exact `gate` named below, and has `status: "pass"`. The validator also enforces these measurements instead of trusting the status alone:

- `edge-relative-performance-efficiency`: Microsoft Edge/same-machine identity; the 110% latency, throughput, CPU, and incremental-memory ratios; per-sample allowance result; cold-font, icon, KaTeX, complex, twelve-visible, cache-hit, UI-publication/callback, retained-growth, handle-growth, cache-budget, memory-pressure, and input-responsiveness ceilings.
- `device-theme-dpi-matrix`: all simulated scales from 100% through 400%, physical 100%/150%/200%, Light/Dark/built-in and custom High Contrast, hardware D3D and WARP, all three process architectures, mixed-DPI moves, text scaling, and HDR composition.
- `worker-fault-injection`: access violation, fail-fast, hang, partial/oversized/wrong/stale responses, pipe loss, OOM, and device loss with typed accessible fallback, unrelated-content recovery, responsiveness, orphan-process, per-architecture Job commit, retained-memory, and handle-growth results.
- `store-child-process-deployment`: NativeAOT, trimming, packaged/unpackaged, signed MSIX, Store policy, child-process launch, kill-on-close, and all three architectures.
- `representative-live-svg-audits`: KaTeX, Flutter, open-ui, Shields badges, and Mermaid plus nested-resource security and accessibility validation.

The generated SPDX files conform to the official [SPDX 2.3 JSON schema](https://github.com/spdx/spdx-spec/blob/v2.3/schemas/spdx-schema.json). Output is sorted, uses normalized LF line endings and UTF-8 without a BOM, and uses a fixed creation timestamp unless the caller deliberately supplies another one.

Run the deterministic fixture and negative-path tests with:

```powershell
./Test-PackageComplianceTools.ps1
```
