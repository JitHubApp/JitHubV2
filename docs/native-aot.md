# Native AOT release contract

JitHub ships `win-x86`, `win-x64`, and `win-arm64` as self-contained Native AOT binaries. Release artifacts must not require CoreCLR, a JIT, managed application assemblies, runtime code generation, or reflection serializer fallbacks.

## Build configurations

- `Debug` remains JIT-enabled for fast F5, Hot Reload, and ordinary development.
- `AotDebug` enables optimized Native AOT with symbols and native debugging. Visual Studio exposes it as `JitHub.WinUI (AotDebug)`.
- `Release` and Store packaging always import `eng/NativeAot.props`. A Release build cannot opt out of trimming or Native AOT.

The shared contract treats compiler, trim, AOT, and CsWinRT warnings as errors. JSON reflection is disabled, configuration binding is source generated, and runtime XAML binding sources use CsWinRT generated custom-property providers.

`AotDebug` intentionally keeps optimization enabled. Unoptimized Native AOT is unsupported, and .NET 10 can deadlock while lazily initializing the WinRT reference-tracker callback during a collection ([dotnet/runtime#121538](https://github.com/dotnet/runtime/issues/121538), fixed on .NET 11 by [dotnet/runtime#121558](https://github.com/dotnet/runtime/pull/121558)). Native symbols remain available for debugger inspection.

## Updating dependencies

Package changes are reviewed per runtime identifier and committed through the lock files:

```powershell
.\eng\Restore-NativeAot.ps1 -Architecture x86 -UpdateLocks
.\eng\Restore-NativeAot.ps1 -Architecture x64 -UpdateLocks
.\eng\Restore-NativeAot.ps1 -Architecture arm64 -UpdateLocks
.\eng\Update-NativeAotDependencyLedger.ps1
```

Review `eng/native-aot-dependencies.json` with the NuGet lock-file changes. CI runs the scripts in locked and verify modes, so an unreviewed direct or transitive runtime package fails the build.

## Local verification

Publish and verify each native payload independently:

```powershell
.\eng\Restore-NativeAot.ps1 -Architecture x64
dotnet publish .\JitHub.WinUI\JitHub.WinUI.csproj -c Release -r win-x64 -p:Platform=x64 --no-restore -o .\artifacts\native-aot\x64
.\eng\Verify-NativeAotArtifact.ps1 -InputPath .\artifacts\native-aot\x64 -Architecture x64
```

Repeat with `x86`/`win-x86` and `ARM64`/`win-arm64`. The verifier rejects CoreCLR and host files, `.deps.json` and `.runtimeconfig.json`, app IL assemblies, CLR PE headers, wrong-machine native dependencies, packaged symbols, and invalid architecture-specific WinRT activation entries.

Build the Store bundle with:

```powershell
.\eng\Build-JitHubWinUIStorePackage.ps1 `
  -ProjectPath .\JitHub.WinUI\JitHub.WinUI.csproj `
  -OutputDirectory .\artifacts\AppPackages `
  -BundlePlatforms 'x86|x64|ARM64'
```

The Store script performs locked restores, runs the release-security gate once, verifies every architecture package, builds a bundle, and verifies the final upload container.

## Dependency policy

- WinUIEdit stays behind the app-owned editor wrapper and consumes its generated NuGet WinMD path.
- SVG rendering uses `Svg.Skia` and `SkiaSharp` through `IRepositorySvgRasterizer` and `AppSvgViewport`; JavaScript and `SkiaSharp.Views.WinUI` are not part of the runtime graph.
- CSV and TSV parsing and presentation are first-party through `CsvDocumentParser` and `AppDataTable`; CsvHelper and the discontinued Toolkit DataGrid are not shipped.
- Store engagement telemetry uses the generated typed projection on x86, x64, and ARM64. Calls run through the app-owned bounded, coalescing dispatcher so Store SDK work is serialized off the UI thread while local diagnostics retain full fidelity.

New runtime reflection, dynamic code generation, expression compilation, runtime generic construction, and reflection JSON overloads are blocked by `eng/BannedSymbols.txt` and the compiler analyzer.

## Release gate

`.github/workflows/native-aot.yml` publishes and packages all three architectures on every change. `.github/workflows/native-aot-hardware-validation.yml` runs the native payload on matching x86, x64, and ARM64 hardware and records UI Automation and screenshot evidence. Store publication requires the successful hardware-validation run ID for the exact commit.

Symbols are uploaded separately. A release is not eligible for Store submission until all architecture publishes have zero AOT, trim, and CsWinRT warnings and the matching-hardware workflow is green.
