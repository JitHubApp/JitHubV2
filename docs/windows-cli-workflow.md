# Windows CLI workflow

JitHub uses three Microsoft command-line tools for different parts of the Windows app lifecycle. They have overlapping names, so keep the responsibilities crisp.

## Tool roles

- `winapp`: Windows App CLI. Use it for local packaged app launch, package-identity debugging, command-line UI automation, and screenshots. Treat it as a preview helper around the app inner loop, not as the source of truth for Store release packages.
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

Policy:

- Do not run `winapp init` against `JitHub.WinUI` casually. It can rewrite project, manifest, asset, and package setup that this repo already owns.
- Keep `dotnet build`, `dotnet msbuild`, and `eng/Build-JitHubWinUIStorePackage.ps1` as the authoritative build/package path for Native AOT and Store upload packages.
- Use `eng/Start-JitHubWinUIDebug.ps1` for the day-to-day command-line Debug launch loop.
- Use `winapp ui` as an additional screenshot and interaction proof layer beside the existing FlaUI design-lab capture pipeline.

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

- `microsoft/microsoft-store-apppublisher@v1.4` installs the pinned `msstore` version on the runner. The workflow passes an explicit upload timeout because the `.appxupload` is sent to Azure blob storage as a single PUT, so the timeout has to cover the whole transfer rather than one chunk; keep the version pin and timeout until a newer CLI release is deliberately validated.
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

- `.github/workflows/winapp-cli-smoke.yml`: manual workflow for verifying that `winapp` is installable and its command surface is available on GitHub-hosted Windows runners. It can optionally build `JitHub.WinUI` after building editor assets.
- `.github/workflows/jithub-store-release.yml`: manual Store package and submission workflow using `msstore`.

## References

- Windows App CLI: https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/
- Windows App CLI reference: https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/usage
- Microsoft Store Developer CLI: https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands
- Store publishing with GitHub Actions: https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/github-actions
- Microsoft Store MSIX package and version requirements: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements
