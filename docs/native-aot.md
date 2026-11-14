# Native AOT release contract

JitHub ships `win-x86`, `win-x64`, and `win-arm64` as self-contained Native AOT binaries. Release artifacts must not require CoreCLR, a JIT, managed application assemblies, runtime code generation, or reflection serializer fallbacks.

## Build configurations

- `Debug` remains JIT-enabled for fast F5, Hot Reload, and ordinary development.
- `AotDebug` enables optimized Native AOT with symbols and native debugging. Visual Studio exposes it as `JitHub.WinUI (AotDebug)`.
- `Release` and Store packaging always import `eng/NativeAot.props`. A Release build cannot opt out of trimming or Native AOT.

The shared contract treats compiler, trim, AOT, and CsWinRT warnings as errors. JSON reflection is disabled, configuration binding is source generated, and runtime XAML binding sources use CsWinRT generated custom-property providers.

`AotDebug` intentionally keeps optimization enabled. Unoptimized Native AOT is unsupported, and .NET 10 can deadlock while lazily initializing the WinRT reference-tracker callback during a collection ([dotnet/runtime#121538](https://github.com/dotnet/runtime/issues/121538), fixed on .NET 11 by [dotnet/runtime#121558](https://github.com/dotnet/runtime/pull/121558)). Native symbols remain available for debugger inspection.

## Pinned .NET SDK

`global.json` names one exact SDK version with `"rollForward": "disable"`. Every build, local and CI, uses that SDK or stops.

The lock files can't protect the graph from the SDK on their own. The SDK adds some packages implicitly, and their versions follow the runtime bundled with the SDK, not anything in the lock files:

- `Microsoft.DotNet.ILCompiler` and `runtime.win-{x86,x64,arm64}.Microsoft.DotNet.ILCompiler`
- `Microsoft.NET.ILLink.Tasks`
- `Microsoft.AspNetCore.App.Internal.Assets`, for `JitHub.Web`

Any SDK with a different runtime patch moves these packages, and every locked restore fails with `NU1004`. Microsoft gives the same advice for lock files: pin the SDK and disable roll-forward ([global.json `rollForward`](https://learn.microsoft.com/dotnet/core/tools/global-json#rollforward), [package lock files](https://learn.microsoft.com/dotnet/core/install/upgrade#package-lock-files)).

How CI gets the pinned SDK: `actions/setup-dotnet@v4` with `global-json-file` reads `sdk.version`. Only for `rollForward: latestFeature` does it shorten the version to `major.minor` and install the newest SDK in that channel. For every other value it installs the exact version. It installs next to the runner image's preinstalled SDKs in `C:\Program Files\dotnet`, so the `dotnet` host still applies `rollForward` when it picks among them. `latestPatch` would pick a newer preinstalled patch as soon as the runner image ships one. `disable` uses the exact SDK or fails.

For local development, install the exact SDK next to any others:

```powershell
winget install Microsoft.DotNet.SDK.10 --version <sdk.version from global.json>
```

If the SDK is missing, `dotnet` stops with `A compatible .NET SDK was not found` and names the requested version. Install that version; don't loosen `rollForward`.

### Moving the SDK pin

Move the pin on purpose, in its own pull request, when a new SDK is needed (for example a .NET servicing release with security fixes). Pick the SDK from the [.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json) and install it. Then, from the repository root:

1. Set `sdk.version` in `global.json`, and check that `dotnet --version` prints it.
2. Regenerate the committed locks of every project by restoring each solution in Release. Debug restores write their lock files under `obj\Debug`, so they don't refresh the committed ones:

   ```powershell
   git ls-files '*.slnx' | ForEach-Object {
       dotnet restore $_ -p:Configuration=Release -p:RestoreLockedMode=false --force-evaluate
   }
   ```

3. Regenerate the Native AOT locks for each architecture. This must come after step 2: a solution restore doesn't publish the renderer libraries for Native AOT, so it drops their ILCompiler packages, and this step puts them back for every runtime identifier:

   ```powershell
   .\eng\Restore-NativeAot.ps1 -Architecture x64 -UpdateLocks
   .\eng\Restore-NativeAot.ps1 -Architecture x86 -UpdateLocks
   .\eng\Restore-NativeAot.ps1 -Architecture arm64 -UpdateLocks
   ```

4. Rebuild the reviewed ledger, then verify it:

   ```powershell
   .\eng\Update-NativeAotDependencyLedger.ps1
   .\eng\Update-NativeAotDependencyLedger.ps1 -Verify
   ```

Review the diff. Only the SDK-implicit packages above should move. If an ordinary `PackageReference` version changed, something other than the SDK drifted; find it before you merge.

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
- SVG rendering uses the statically referenced `MarkdownRenderer.Svg.Resvg` provider and its architecture-specific isolated worker through `IRepositorySvgRasterizer` and `AppSvgViewport`; browser hosting, JavaScript, ThorVG, Svg.Skia, and SkiaSharp are not part of the runtime graph.
- CSV and TSV parsing and presentation are first-party through `CsvDocumentParser` and `AppDataTable`; CsvHelper and the discontinued Toolkit DataGrid are not shipped.
- Store engagement telemetry uses the generated typed projection on x86, x64, and ARM64. Calls run through the app-owned bounded, coalescing dispatcher so Store SDK work is serialized off the UI thread while local diagnostics retain full fidelity.

New runtime reflection, dynamic code generation, expression compilation, runtime generic construction, and reflection JSON overloads are blocked by `eng/BannedSymbols.txt` and the compiler analyzer.

## Release gate

`.github/workflows/native-aot.yml` publishes, verifies, and packages all three architectures on every push and pull request. Store publication relies on that reviewed PR gate and rebuilds the locked Native AOT x86, x64, and ARM64 bundle in `.github/workflows/jithub-store-release.yml`; it does not require a separate hardware-validation run or run ID.

Symbols are uploaded separately. A release is eligible for Store submission after the PR architecture matrix has zero AOT, trim, and CsWinRT warnings and the Store package verifier passes.
