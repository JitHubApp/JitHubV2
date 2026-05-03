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

This command uses the documented .NET debug-identity flow: `dotnet build`, then `winapp create-debug-identity`, then direct executable launch. It keeps the normal Debug output shape while giving the app package identity for APIs and protocol registrations that need it.

Useful variants:

```powershell
.\eng\Start-JitHubWinUIDebug.ps1 -Platform ARM64
.\eng\Start-JitHubWinUIDebug.ps1 -NoLaunch
.\eng\Start-JitHubWinUIDebug.ps1 -SkipBuild
.\eng\Start-JitHubWinUIDebug.ps1 -AppArguments '--page=design-lab', '--theme=dark'
```

By default the script lets `winapp create-debug-identity` append its debug suffix so local Debug builds do not collide with an installed Store build. Use `-KeepIdentity` only when intentionally testing the exact manifest identity.

Build, launch, wait for the app, and capture a screenshot:

```powershell
.\eng\Invoke-WinAppCliSmoke.ps1
```

If you only want to verify command availability without launching the app:

```powershell
.\eng\Invoke-WinAppCliSmoke.ps1 -SkipBuild -SkipLaunch
```

The smoke script assumes editor assets already exist under `artifacts/EditorAssets/dist`. If they do not, run:

```powershell
.\sync-vscode-assets.ps1
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

It now uses the Microsoft Store Developer CLI as the release control plane:

- `microsoft/microsoft-store-apppublisher@v1.3` installs `msstore` on the runner.
- `msstore reconfigure` authenticates with the protected `microsoft-store` GitHub environment secrets.
- `msstore publish` receives the exact `.appxupload` or `.msixupload` file produced by `eng/Build-JitHubWinUIStorePackage.ps1` through `--inputFile`.
- `store_submission_mode` controls whether the run publishes publicly, keeps a draft, or targets a flight.
- `store_flight_id` is required when `store_submission_mode` is `flight`.
- `package_rollout_percentage` can stage rollout from `0` to `100`.

Recommended modes:

- `draft`: use for release rehearsals. This validates package upload without committing the submission.
- `flight`: use for internal/beta validation while the WinUI app is still hardening.
- `public`: use only when the build is release-ready and the `microsoft-store` environment approval has been reviewed.

The workflow remains manually triggered and guarded by `allow_winui_store_release` until the WinUI migration reaches full feature parity.

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
