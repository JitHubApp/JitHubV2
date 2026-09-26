# Windows CLI workflow

JitHub uses three Microsoft command-line tools for different parts of the Windows app lifecycle. They have overlapping names, so keep the responsibilities crisp.

## Tool roles

- `winapp` 0.7: Windows App CLI. Use it for project-mode launch, package-identity debugging, isolated UI automation, screenshots, and project-resolved API discovery. The repository's Store release scripts remain the source of truth for Store upload packages.
- `msstore`: Microsoft Store Developer CLI. Use it for Partner Center automation: configuring credentials, publishing Store submissions, drafts, flights, rollout percentages, and future metadata-as-code work.
- `store`: Microsoft Store client CLI. Use it as a user-facing smoke test after release: verify the public listing, search result, install, and update behavior from a normal Windows machine.

## Local setup

Check which tools are present:

```powershell
.\eng\Ensure-WindowsCliTools.ps1
```

Install missing tools with WinGet where possible:

```powershell
.\eng\Ensure-WindowsCliTools.ps1 -InstallMissing
```

Notes:

- `winapp` is installed from the `Microsoft.WinAppCli` WinGet package.
- `msstore` is installed from the Microsoft Store Developer CLI package and requires the .NET 9 Desktop Runtime.
- `store` ships with the Microsoft Store experience on supported Windows builds. If it is missing, update Microsoft Store rather than vendoring anything into this repo.

## Windows App CLI usage

Use `winapp` to verify the packaged app inner loop and UI automation from a terminal.

Basic command-surface check:

```powershell
winapp --help
winapp create-debug-identity --help
winapp ui --help
winapp target --help
winapp find-api --help
```

Build Debug, apply package identity, and launch JitHub:

```powershell
.\eng\Start-JitHubWinUIDebug.ps1
```

This command uses the documented .NET debug-identity flow: `dotnet build`, guarded cleanup of stale development registrations, `winapp create-debug-identity`, then direct executable launch. Debug builds use the dedicated `JitHub.WinUI.Debug` package identity and `jithub-dev://` OAuth callback. Store and Release builds retain `54742Neromarah.JitHub` and are the only builds that register `jithub://`.

Useful variants:

```powershell
.\eng\Start-JitHubWinUIDebug.ps1 -Platform ARM64
.\eng\Start-JitHubWinUIDebug.ps1 -NoLaunch
.\eng\Start-JitHubWinUIDebug.ps1 -SkipBuild
.\eng\Start-JitHubWinUIDebug.ps1 -SkipIdentityCleanup
.\eng\Start-JitHubWinUIDebug.ps1 -AppArguments '--page=design-lab', '--theme=dark'
```

To remove stale Debug registrations without building or launching the app, run:

```powershell
.\eng\Reset-JitHubWinUIDebugIdentity.ps1
```

The cleanup script removes development-mode JitHub packages only. It never removes a normally installed Store package.

Build, launch, wait for the app, and capture a screenshot:

```powershell
.\eng\Invoke-WinAppCliSmoke.ps1
```

If you only want to verify command availability without launching the app:

```powershell
.\eng\Invoke-WinAppCliSmoke.ps1 -SkipBuild -SkipLaunch
```

### Isolated renderer UI automation (WinApp CLI 0.7)

On a Windows 11 24H2+ development machine with Windows Sandbox and virtualization enabled, run:

```powershell
.\MarkdownRenderer\eng\Test-SandboxSampleUi.ps1
```

The script uses `winapp run <sample.csproj> --on sandbox --detach` to build on the host, deploy and launch inside the sandbox, then uses only sandbox-targeted UI commands. It checks Math, Mermaid, safe HTML, and SVG navigation and document content, checks the Mermaid native-engine diagnostic, and writes screenshots under `artifacts/winapp-sandbox`. `-NoBuild` reuses the existing Debug x64 build. The script uses a workflow ID and yields its UI turn at the end; it does **not** stop or reset an existing sandbox. Review the screenshots for visual fidelity—UIA text assertions alone do not establish that diagrams or images painted correctly.

The sandbox is a real isolation boundary, not a fallback to the host. A missing native runtime in the clean guest is a product/deployment issue, not a reason to silently rerun the test locally. Use `winapp target snapshot sandbox --json` to inspect readiness and guest windows; `winapp target exec`, `push`, and `pull` are for targeted diagnostics or moving test evidence. `winapp target screenshot sandbox` captures the whole guest desktop; the script captures the sample window instead. For intermittent interactions, `winapp ui record --on sandbox` or `winapp target record sandbox` can save a bounded video to the host. Do not call `winapp target stop sandbox` unless you intend to terminate that sandbox and its state.

For the packaged JitHub Debug shell, run `eng/Invoke-WinAppCliSmoke.ps1 -Sandbox`. It provisions the Microsoft-supplied Debug x64 VC framework from the installed Windows SDK *inside the guest* if that clean sandbox lacks it, launches JitHub there, waits for the accessible sign-in control, and captures a host-side screenshot. `-SkipBuild` reuses an existing Debug build. Add `-Aot` to perform a locked AotDebug project restore and then exercise WinApp CLI 0.7's project-mode `--aot --no-restore` sandbox launch; it hashes the guest process executable against the native publish output and checks the same accessible sign-in shell. The guest has no personal GitHub credentials, so these smokes cover the unauthenticated shell only; authenticated pages require a separate controlled test account and must not copy a developer's tokens into the sandbox. The SDK prerequisite is distinct from the Markdown renderer's Rust native engines, which now link the VC runtime statically for clean deployments.

Static CRT linkage avoids a machine-global VC runtime dependency for those native binaries, but requires rebuilding and redeploying them when Microsoft ships relevant runtime fixes. The Debug VC framework package is used only in the local sandbox; it is not added to NuGet packages or Store uploads.

GitHub-hosted `windows-latest` runners are not assumed to provide nested virtualization or an interactive sandbox desktop. The `winapp-cli-smoke` workflow pins CLI 0.7 and checks its command surface; run the isolated UI suite on a Windows 11 self-hosted runner with Sandbox enabled before treating it as a CI release gate. A locked or headless session cannot satisfy touch or screenshot tests.

### Other 0.7 capabilities

- The app's Debug configuration and the sample reference the MIT-licensed `Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer` 0.7 package as a private build-only dependency. This makes its WinUI/MVVM/interop diagnostics available in IDE and optional Debug CI builds without adding runtime assets. The first Debug x64 app build reports 108 existing analyzer warnings, chiefly implicit OneTime `x:Bind`; these need binding-by-binding review rather than a blanket suppression or automatic `Mode=OneWay` rewrite. Release and AotDebug keep their existing warning-as-error build and locked dependency graph until those findings are resolved.
- UI tests should use scoped `--root`, typed `--type`/`--class-name` queries, and exact `invoke --action` when the UIA contract supports them. The sandbox script exercises those forms to avoid matching a similarly named control elsewhere in the page.
- After restoring `JitHub.WinUI`, `winapp find-api check-property NavigationView IsBackButtonVisible --project-dir .\JitHub.WinUI --json` validates the project's referenced WinUI API surface. The optional Debug-build workflow step executes this check; use `members` and `enums` during XAML/API work instead of guessing SDK members.
- `winapp run --aot` works for this configured Native AOT project when preceded by a locked *project* restore and invoked with `--no-restore`; `eng/Invoke-WinAppCliSmoke.ps1 -Sandbox -Aot` codifies that verified path. Its default solution-wide single-RID restore conflicts with this repository's multi-RID locked graphs, so do not omit `--no-restore`. Keep the existing Store publish scripts authoritative: they set app-specific identity, runtime, signing, and upload properties that the generic launch command does not replace.
- `winapp package <project.csproj>` builds a disposable development MSIX. The optional CLI smoke workflow exercises an unsigned Debug x64 package; do not distribute it. It is **not** interchangeable with `eng/Build-JitHubWinUIStorePackage.ps1`, which builds the Native AOT multi-architecture Store upload bundle and enforces this repository's signing and version rules.
- Single-file .NET app mode and the experimental Reactor/MVU templates target new app shapes; this established XAML/MVVM solution should not be migrated merely to use them. The standalone UIAutomation NuGet packages are optional for embedding an automation engine; the current WinApp CLI and existing FlaUI harness already cover this repo's CLI automation needs.

Policy:

- Do not run `winapp init` against `JitHub.WinUI` casually. It can rewrite project, manifest, asset, and package setup that this repo already owns.
- Keep `dotnet build`, `dotnet msbuild`, and `eng/Build-JitHubWinUIStorePackage.ps1` as the authoritative build/package path for Native AOT and Store upload packages.
- Use `eng/Start-JitHubWinUIDebug.ps1` for the day-to-day command-line Debug launch loop.
- Use `winapp ui` as an additional screenshot and interaction proof layer beside the existing FlaUI design-lab capture pipeline. Prefer `--on sandbox` for repeatable isolated local runs.

## Microsoft Store Developer CLI usage

Verify that `msstore` is available:

```powershell
.\eng\Test-MicrosoftStoreDeveloperCli.ps1
```

Verify local Partner Center configuration when needed:

```powershell
.\eng\Test-MicrosoftStoreDeveloperCli.ps1 -RequireConfigured
```

Configure credentials locally only when you intentionally need Partner Center access:

```powershell
msstore reconfigure `
  --tenantId $env:STORE_TENANT_ID `
  --sellerId $env:STORE_SELLER_ID `
  --clientId $env:STORE_CLIENT_ID `
  --clientSecret $env:STORE_CLIENT_SECRET
```

Never put Partner Center credentials in plain text files, checked-in scripts, or logs.

## Store release workflow

The Store release workflow is `.github/workflows/jithub-store-release.yml`.

Store package versions use `Major.Minor.Build.0`. Microsoft reserves the fourth component for Store use, so release operators must increment Major, Minor, or Build and leave Revision at `0`. The workflow validates the version immediately after checkout, before restoring or building the Native AOT package.

It now uses the Microsoft Store Developer CLI as the release control plane:

- `microsoft/microsoft-store-apppublisher@v1.4` installs the pinned `msstore` version on the runner. The workflow passes an explicit upload timeout because `msstore` v0.4.1 otherwise resolves the omitted option to zero; keep the version pin and timeout until a newer CLI release is deliberately validated.
- `msstore reconfigure` authenticates with the protected `microsoft-store` GitHub environment secrets.
- `msstore publish` receives the exact `.appxupload` or `.msixupload` file produced by `eng/Build-JitHubWinUIStorePackage.ps1` as the publish command's positional input.
- `use_signing_certificate` is optional. Leave it `false` to match the existing UWP Store-upload flow where Partner Center accepts and re-signs the submitted package; enable it only when `STORE_PACKAGE_CERTIFICATE_BASE64` and `STORE_PACKAGE_CERTIFICATE_PASSWORD` are configured.
- `JITHUB_STORE_BUNDLE_PLATFORMS` defaults to `x64|ARM64`. The packaging script builds each architecture independently, then creates one `.msixupload` containing both architecture packages so `msstore publish <package.msixupload>` can submit the release without invoking the broken raw-asset multi-platform bundle indexing path.
- `store_submission_mode` controls whether the run publishes publicly, keeps a draft, or targets a flight.
- `store_flight_id` is required when `store_submission_mode` is `flight`.
- `package_rollout_percentage` can stage rollout from `0` to `100`.

Recommended modes:

- `draft`: use for release rehearsals. This validates package upload without committing the submission.
- `flight`: use for internal/beta validation while the WinUI app is still hardening.
- `public`: use only when the build is release-ready and the `microsoft-store` environment approval has been reviewed.

The workflow remains manually triggered and should normally be run as `draft` first, then rerun as `public` after the Partner Center submission details are reviewed.

## Store client CLI usage

Use `store` from a normal Windows machine to confirm what users can see after release.

Check the public listing and search result:

```powershell
.\eng\Test-StoreListing.ps1
```

Useful manual checks:

```powershell
store show 9MXRBJBB552V
store search JitHub
store install 9MXRBJBB552V
store update 9MXRBJBB552V
```

Do not use `store` as a publishing tool. It is a client-side Store surface, not Partner Center automation.

## CI workflows

- `.github/workflows/winapp-cli-smoke.yml`: manual workflow pinned to `winapp` 0.7 for command-surface verification on GitHub-hosted Windows runners. It can optionally build `JitHub.WinUI` and check its resolved API surface. It does not claim sandbox UI coverage on a hosted runner.
- `.github/workflows/jithub-store-release.yml`: manual Store package and submission workflow using `msstore`.

## References

- Windows App CLI: https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/
- Windows App CLI reference: https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/usage
- WinApp CLI 0.7 announcement: https://devblogs.microsoft.com/ifdef-windows/winappcli-v0-7-0-release-announcement/
- Windows Sandbox execution guide: https://github.com/microsoft/winappCli/blob/main/docs/sandbox-execution.md
- Microsoft C++ deployment methods: https://learn.microsoft.com/en-us/cpp/windows/deployment-in-visual-cpp
- Microsoft Store Developer CLI: https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands
- Store publishing with GitHub Actions: https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/github-actions
- Microsoft Store MSIX package and version requirements: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements
