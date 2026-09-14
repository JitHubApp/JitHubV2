# MarkdownRenderer package compliance tooling

Run all package checks against a fresh package output directory:

```powershell
./Invoke-PackageCompliance.ps1 `
    -PackageDirectory ../artifacts/packages `
    -OutputDirectory ../artifacts/package-compliance
```

For a release gate, add `-FailOnUnknownLicense`. The ordinary mode still writes all evidence but records unknown package or dependency licenses as `NOASSERTION` in the SPDX document, notices report, and summary. It never infers a license from a package name. Produced sibling packages are resolved by exact package ID and version, and their dependency graphs are traversed, so direct and transitive declarations and embedded notice assets can be carried into each package report.

Every invocation also applies `license-compatibility-policy.json` to the generated NuGet SPDX closure and the vendored/native Math, Mermaid, ThorVG, and TextMate inventories. A known license that is absent from the reviewed allowlist fails just like `NOASSERTION`; known metadata is not treated as compatibility approval. The four MPL-2.0 Rust crates are pinned as an exact reviewed set and require the packaged source-availability notice. Any change to that set fails closed. The optional EPL-licensed ELK Mermaid layout must remain excluded.

The output includes one SPDX 2.3 JSON SBOM and one notices-evidence report per nupkg, a summary of license findings, `license-compatibility.json`, a selected-RID native/ABI report, and a deterministic package manifest containing package and archive-entry hashes and sizes. Supply a manifest from an independent build through `-ReferenceManifest` to require byte-for-byte package reproducibility.

`Test-NativePackageCompliance.ps1` verifies each native DLL's PE machine against its `win-x86`, `win-x64`, or `win-arm64` path. Every native asset must have an exact exported-symbol allowlist. Use `-InventoryOnly` to review a new native payload without weakening the normal gate; update the checked-in allowlist only after reviewing the ABI change. A package configured with `allowNoNative` is reported explicitly when no native DLL exists.

The generated SPDX files conform to the official [SPDX 2.3 JSON schema](https://github.com/spdx/spdx-spec/blob/v2.3/schemas/spdx-schema.json). Output is sorted, uses normalized LF line endings and UTF-8 without a BOM, and uses a fixed creation timestamp unless the caller deliberately supplies another one.

Run the deterministic fixture and negative-path tests with:

```powershell
./Test-PackageComplianceTools.ps1
```
