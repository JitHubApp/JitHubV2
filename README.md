<p align="center">
  <span><img src="JitHub.Web/wwwroot/JitHubLogo.png" alt="JitHub Logo" width="96" height="96"></span>
  <h1 align="center">JitHub</h1>
</p>

<p align="center">
  JitHub is a packaged Windows App SDK / WinUI 3 GitHub client for Windows. It brings repositories, issues, pull requests, activity, and code browsing into a quieter native desktop app.
</p>

<p align="center">
  <a href="https://apps.microsoft.com/store/detail/jithub/9MXRBJBB552V">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Download JitHub" width="128" />
  </a>
</p>

## What JitHub Does

- Browse public, private, and forked repositories from a native Windows shell
- Search GitHub repositories and open exact `owner/name` matches quickly
- Read repository activity, issues, pull requests, comments, reactions, labels, and timelines
- Star, fork, watch, create, edit, close, merge, and react without leaving the app
- Browse files, branches, commits, and repository content in an embedded VS Code-based surface
- Use a calm app-owned design system with light and dark themes, design-lab coverage, and screenshot automation

## Screenshots

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="JitHub.Web/wwwroot/ss1-dark.png">
  <img src="JitHub.Web/wwwroot/ss1.png" alt="JitHub home dashboard and repository workspace." width="900">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="JitHub.Web/wwwroot/ss2-dark.png">
  <img src="JitHub.Web/wwwroot/ss2.png" alt="JitHub code page with the embedded VS Code experience." width="900">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="JitHub.Web/wwwroot/ss3-dark.png">
  <img src="JitHub.Web/wwwroot/ss3.png" alt="JitHub pull request and issue management surfaces." width="900">
</picture>

## Current Architecture

- `JitHub.WinUI`: the desktop app, built with .NET 10, Windows App SDK, WinUI 3, packaged MSIX identity, and Native AOT-friendly service/model patterns
- `JitHub.Web`: the unified ASP.NET Core website, landing page, OAuth callback page, and `/api/GithubCodeToToken` token-exchange API
- `JitHub.WinUI.Automation`: the FlaUI-based screenshot and smoke-test harness used with the in-app design lab
- `artifacts/EditorAssets/dist`: generated editor assets copied from `jithub-vs-code` during development and CI; these files are not checked in
- `eng`: repeatable local and CI scripts for Windows App CLI, Microsoft Store CLI, App Service provisioning, editor asset sync, and release packaging

## Runtime Shape

- The desktop app authenticates through a browser-based GitHub OAuth flow, then receives the token through the `jithub://auth/v2` protocol callback.
- The public website is static server-rendered by default. The `/authorize` route uses a tiny JavaScript bridge only for OAuth handoff; it does not ship Blazor WebAssembly.
- The web project is deployed as a normal Azure App Service Web App. It replaces the old split Static Web App plus Function App shape.
- The desktop release workflow builds Store packages for `x64` and `ARM64`, prepares editor assets from `jithub-vs-code`, and publishes through Microsoft Store Developer CLI.
- The app UI is driven by semantic WinUI resource dictionaries and reusable app-owned controls, with design-lab screenshot proof for light and dark themes.

`global.json` pins the repo to .NET SDK `10.0.202`.

## Build from source

Use the latest Visual Studio 2022 with these workloads:

- .NET desktop development
- Windows application development

You also need:

- .NET 10 SDK
- Node.js
- Yarn

Optional Windows CLI tools are documented in `docs/windows-cli-workflow.md`:

- `winapp` for packaged app launch, debug identity, UI automation, and screenshot smoke checks
- `msstore` for Microsoft Store Partner Center publishing, drafts, flights, rollout, and future metadata automation
- `store` for user-facing Store listing, search, install, and update smoke checks

Check or install the tools with:

```powershell
.\eng\Ensure-WindowsCliTools.ps1
.\eng\Ensure-WindowsCliTools.ps1 -InstallMissing
```

### Desktop app setup

`JitHub.WinUI/appsettings.json` is checked in with public OAuth client IDs only. GitHub OAuth client IDs are not secrets; client secrets must stay in user secrets, environment variables, Azure App Service settings, or GitHub Actions secrets.

```json
{
  "Credential": {
    "ClientId": "<production client ID>",
    "DevelopmentClientId": "Ov23libqduSlPx5TcCne",
    "DevelopmentAuthorizationCallbackUrl": "https://localhost:7284/authorize"
  }
}
```

Debug builds use `DevelopmentClientId` and `DevelopmentAuthorizationCallbackUrl` automatically so local app launches authenticate against the development GitHub OAuth app. Release builds keep using `ClientId`. To force the development app in a non-Debug local run, set `JITHUB_USE_DEV_OAUTH_APP=true`; to override either path with a specific OAuth app, set `JITHUB_OAUTH_CLIENT_ID` and optionally `JITHUB_OAUTH_CALLBACK_URL`.

Set these settings for the web callback token exchange:

- Local Development uses the development GitHub OAuth app automatically and ignores production-style `JitHubClientId` / `JithubAppSecret` environment variables.
- Set the development app secret with `dotnet user-secrets set JITHUB_DEV_OAUTH_CLIENT_SECRET "<secret>" --project .\JitHub.Web\JitHub.Web.csproj`.
- Production uses `JitHubClientId` or legacy fallback `JithubClientId`, plus `JithubAppSecret` or `GitHubOAuth__ClientSecret`.
- To intentionally test the production OAuth app from a local Development web host, set `JITHUB_WEB_USE_PRODUCTION_OAUTH=true`.

### Editor assets

Build the editor assets from [jithub-vs-code](https://github.com/nerocui/jithub-vs-code) by running `./sync-vscode-assets.ps1` in PowerShell.

The script looks for a local `jithub-vs-code` clone, runs `yarn --frozen-lockfile` and `yarn build`, then copies the generated `dist` output into `artifacts/EditorAssets/dist`. Those generated files are intentionally gitignored.

`JitHub.WinUI` fails build, publish, and packaging if `artifacts/EditorAssets/dist/index.html` is missing, and the app loads the copied files directly from `Assets/dist`.

### Local website development

The public website is now an ASP.NET Core server app. It serves the landing page, `/authorize`, and `/api/GithubCodeToToken` from one host.

Run it locally with:

```powershell
dotnet run --project .\JitHub.Web\JitHub.Web.csproj --launch-profile https
```

For the development GitHub OAuth app, set the callback URL to:

```text
https://localhost:7284/authorize
```

The callback route is `/authorize`, not `/auth/callback`. The authorize page calls the same-origin `/api/GithubCodeToToken` endpoint to exchange the temporary GitHub code, then launches the app through the `jithub://` protocol callback.

The local callback host needs the development OAuth app secret before sign-in can complete:

```powershell
dotnet user-secrets set JITHUB_DEV_OAUTH_CLIENT_SECRET "<secret>" --project .\JitHub.Web\JitHub.Web.csproj
```

The website does not require `wasm-tools`. The landing page is static SSR, and the authorize flow uses a tiny JavaScript bridge instead of Blazor WebAssembly.

### Running the app locally

After editor assets are present, open `JitHub.slnx` in Visual Studio and run the packaged `JitHub.WinUI` project.

To build Debug, apply a debug package identity with the Windows App CLI, and launch the app from the terminal, run:

```powershell
.\eng\Start-JitHubWinUIDebug.ps1
```

This builds `JitHub.WinUI` as `Debug|x64`, runs `winapp create-debug-identity` against the built executable, and launches `JitHub.WinUI.exe`.

To launch a different platform or pass app arguments:

```powershell
.\eng\Start-JitHubWinUIDebug.ps1 -Platform ARM64
.\eng\Start-JitHubWinUIDebug.ps1 -AppArguments '--page=design-lab', '--theme=dark'
```

### Design lab and screenshot proof

The desktop app now includes a dev-only `DesignLabPage` plus a small UI automation harness for screenshot proof.

Generate the current light/dark screenshot matrix with:

```powershell
.\capture-winui-design.ps1
```

Artifacts are written to:

- `artifacts/screenshots/winui/index.html`
- `artifacts/screenshots/winui/*.png`

The capture script builds `JitHub.WinUI`, launches scenario-specific pages with launch arguments such as `--page=design-lab`, `--scenario=buttons`, and `--theme=dark`, and then uses the `JitHub.WinUI.Automation` project to capture deterministic UI states through FlaUI.

`winapp ui` is also available as a lightweight command-line proof path. Use `.\eng\Invoke-WinAppCliSmoke.ps1` for quick launch/wait/screenshot validation; keep the FlaUI design-lab harness for the full deterministic matrix.

## Web deployment

`JitHub.Web` deploys as a normal ASP.NET Core App Service Web App. The old production `jithubauth.azurewebsites.net` host was an Azure Function App, so the unified website needs a real Web App provisioned before the first production deployment.

The workflow is:

- `.github/workflows/main_jithubweb.yml`

It builds `JitHub.Web` in Release mode and deploys the published output to the Web App named by the `JITHUB_WEBAPP_NAME` repository variable. If the variable is not set, the workflow defaults to `jithub-web-prod`.

Before enabling deployment:

1. Provision a regular Windows App Service Web App with .NET 10. Use `.\eng\Provision-JitHubWebApp.ps1` for the safe setup path.
2. If `jithubauth.azurewebsites.net` must remain the compatibility callback host, retire the old Function App first so the new Web App can reuse the `jithubauth` name.
3. Configure the Web App settings `JitHubClientId` and `JithubAppSecret` from the GitHub OAuth app used by the existing callback host.
4. Download the Web App publish profile and save it as the GitHub secret `JITHUB_WEBAPP_PUBLISH_PROFILE`.
5. Save the target app name as the GitHub variable `JITHUB_WEBAPP_NAME`.
6. Move `jithub.zhuowencui.com` from the old Static Web App to the new Web App after the new host passes a smoke test.

## Microsoft Store release workflow

The repository includes a manual GitHub Actions workflow at `.github/workflows/jithub-store-release.yml` for building a Store upload package and publishing it to Partner Center in one run.

Set up a protected GitHub environment named `microsoft-store` and configure these secrets there:

- `STORE_PRODUCT_ID`
- `STORE_SELLER_ID`
- `STORE_TENANT_ID`
- `STORE_CLIENT_ID`
- `STORE_CLIENT_SECRET`
- `STORE_PACKAGE_IDENTITY_NAME`
- `STORE_PACKAGE_PUBLISHER`

Optional secrets:

- `STORE_PHONE_PRODUCT_ID`
- `STORE_PHONE_PUBLISHER_ID`
- `STORE_PACKAGE_CERTIFICATE_BASE64`
- `STORE_PACKAGE_CERTIFICATE_PASSWORD`
- `STORE_PACKAGE_CERTIFICATE_THUMBPRINT`

Optional environment variables:

- `STORE_APP_DISPLAY_NAME`
- `STORE_PUBLISHER_DISPLAY_NAME`
- `JITHUB_STORE_BUNDLE_PLATFORMS` (defaults to `x64|ARM64`)

The WinUI Store workflow now targets `x64|ARM64` by default. The script builds each architecture as a separate Store package and then creates one `.msixupload` containing both architecture packages, which keeps the submission on the documented Microsoft Store CLI `--inputFile` path without asking MSBuild to resource-index the raw editor asset tree as a multi-platform bundle. `x86` is intentionally not included.

Run the **Publish JitHub to Microsoft Store** workflow manually and provide a four-part `release_version` such as `1.6.5.0`. The workflow checks out `nerocui/jithub-vs-code`, builds the editor assets into `artifacts/EditorAssets/dist`, patches `JitHub.WinUI/Package.appxmanifest` at runtime from the configured environment values, builds a Store upload package, uploads the build artifacts, and then publishes the generated `.appxupload` or `.msixupload` to the Microsoft Store.

The workflow uses the Microsoft Store Developer CLI (`msstore`) as the Store control plane. The `store_submission_mode` input controls the submission target:

- `draft`: upload the package but keep the Partner Center submission uncommitted
- `flight`: publish to the configured `store_flight_id`
- `public`: submit the package publicly

Use `package_rollout_percentage` for staged rollout when Partner Center should release the package gradually.

After a release, verify the public Store listing from a Windows machine with:

```powershell
.\eng\Test-StoreListing.ps1
```

For the detailed CLI policy and command reference, see `docs/windows-cli-workflow.md`.

## Contributing

1. Fork this repository and clone it locally.
2. Create a branch for your feature or bug fix.
3. Make your changes and commit them with a descriptive message.
4. Push your branch to your fork.
5. Open a pull request against `main`.

Please follow [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) and [CODING_STYLE.md](CODING_STYLE.md).

## License

JitHub is licensed under the MIT License. See [LICENSE](LICENSE) for details.
