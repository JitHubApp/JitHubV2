<p align="center">
  <span><img src="JitHub.Web/wwwroot/JitHubLogo.png" alt="JitHub Logo" width="96" height="96">
  <h1 align="center">JitHub</h1></span>
</p>



<p align="center">
  JitHub is a packaged WinAppSDK / WinUI 3 app for GitHub lovers 💖. It lets you browse GitHub repositories, issues, pull requests, and more on your Windows device. It is designed to be smooth, fluid, and native, using WinUI for the look and feel and optimized for touch. JitHub is fast, beautiful, and powerful 💪.
</p>

<p align="center">
  <a href="https://apps.microsoft.com/store/detail/jithub/9MXRBJBB552V">
   <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Download JitHub" width="128"/>
</a>
</p>

## Features 🎁

- Browse GitHub repositories by topics, languages, or keywords 🔎
- View repository details, files, commits, issues, pull requests, and actions 📝
- Star, fork, watch, or clone any repository ⭐
- Create, edit, or close issues and pull requests ✅
- Comment on issues and pull requests with Markdown support 🗣️
- Manage your notifications and profile 🔔
- Switch between light and dark themes 🌞🌙

## Screenshots 📸

<img src="https://github.com/JitHubApp/JitHubV2/blob/main/ScreenShots/screenshot1.png" width="640"/>
<img src="https://github.com/JitHubApp/JitHubV2/blob/main/ScreenShots/screenshot2.png" width="640"/>
<img src="https://github.com/JitHubApp/JitHubV2/blob/main/ScreenShots/screenshot3.png" width="640"/>

## Installation 💾

You can download JitHub from the Microsoft Store or build it from source.

### Microsoft Store

[Get JitHub from the Microsoft Store](https://apps.microsoft.com/store/detail/jithub/9MXRBJBB552V) and enjoy the best GitHub experience on Windows 😍.

### Build from source

To build JitHub from source, use the latest Visual Studio 2022 with these workloads:

- .NET desktop development 🖥️
- Windows application development 🪟

You also need the .NET 10 SDK, the WebAssembly build tools workload, Node.js, and Yarn available on your machine.

The migrated desktop app is powered by the following core packages and platforms:

- Microsoft.WindowsAppSDK 🎨
- Microsoft.Web.WebView2 🌐
- Octokit 🐙
- Windows.Services.Store 🛍️

Then, create a file named `appsettings.json` in the `JitHub.WinUI` project folder with the following content. Go to [settings > Developer settings](https://github.com/settings/developers) and create an OAuth app. Name it however you like, and in there you can get your clientID and secret. Copy them into the `appsettings.json` file. This file is gitignored, so please never commit it.
```json
{
  "Credential": {
    "ClientId": "<your client ID>"
  }
}
```

Finally, set environment variable `JithubAppSecret` to your GitHub secret and `JitHubClientId` to your GitHub client ID. The Native AOT auth host reads those values at runtime.

Now you need to build the editor assets from [jithub-vs-code](https://github.com/nerocui/jithub-vs-code). Run `.\sync-vscode-assets.ps1` in PowerShell. The script looks for a local `jithub-vs-code` clone (including `E:\jithub-vs-code`), runs `yarn --frozen-lockfile` and `yarn build`, and then copies the generated `dist` output into `artifacts\EditorAssets\dist`. Those generated files are intentionally gitignored. `JitHub.WinUI` fails build, publish, and packaging if `artifacts\EditorAssets\dist\index.html` is missing, and the app now loads the copied files directly from `Assets\dist` instead of repackaging them into a zip.

After that, install the WebAssembly tools with `dotnet workload install wasm-tools`.

If you are working on the browser auth callback locally, run `dotnet run --project .\JitHub.Auth\JitHub.Auth.csproj --launch-profile JitHub.Auth` so the auth API is available at `http://localhost:7003`.

`JitHub.Web\wwwroot\appsettings.json` points production browser auth traffic at `https://jithubauth.azurewebsites.net/`. If you deploy the auth host under a different hostname, update `API_Prefix` to match. `JitHub.Auth\appsettings.json` allows Azure Static Web Apps origins by default; add explicit values under `Cors:AllowedOrigins` if you serve the browser callback from a custom HTTPS domain.

When deploying `JitHub.Auth` to **Azure Web App for Linux** with a publish profile, set the app's **Startup Command** in Azure itself (Portal or CLI) to:

```bash
bash -c 'cd /home/site/wwwroot && cp ./JitHub.Auth /tmp/JitHub.Auth && chmod +x /tmp/JitHub.Auth && exec /tmp/JitHub.Auth'
```

The `azure/webapps-deploy` action cannot apply Linux startup commands when it authenticates with a publish profile, so this is a one-time infrastructure setting outside the repository workflow.

After that, open `JitHub.slnx` in Visual Studio and run the packaged `JitHub.WinUI` app.

### Microsoft Store release workflow

The repository now includes a manual GitHub Actions workflow at `.github/workflows/jithub-store-release.yml` for building a Store upload package and publishing it to Partner Center in one run.

The workflow is currently **guarded by default** during the WinUI migration. It will fail unless `allow_winui_store_release` is explicitly set to `true`, because Store publishing should stay blocked until the WinUI app reaches full feature parity.

JitHub is currently published from an **individual** Partner Center account. Full automation is still possible, but Microsoft Store publishing uses the same Microsoft Entra application flow that organization accounts use. That means the GitHub environment must contain the Partner Center product identity values **and** a working Entra tenant/app registration that has been granted access in Partner Center.

Set up a protected GitHub environment named `microsoft-store` and configure these secrets there:

- `STORE_PRODUCT_ID`
- `STORE_SELLER_ID`
- `STORE_TENANT_ID`
- `STORE_CLIENT_ID`
- `STORE_CLIENT_SECRET`
- `STORE_PACKAGE_IDENTITY_NAME`
- `STORE_PACKAGE_PUBLISHER`
- `STORE_PACKAGE_CERTIFICATE_BASE64`
- `STORE_PACKAGE_CERTIFICATE_PASSWORD`

Optional secrets:

- `STORE_PHONE_PRODUCT_ID`
- `STORE_PHONE_PUBLISHER_ID`
- `STORE_PACKAGE_CERTIFICATE_THUMBPRINT`

Optional environment variables:

- `STORE_APP_DISPLAY_NAME`
- `STORE_PUBLISHER_DISPLAY_NAME`
- `JITHUB_STORE_BUNDLE_PLATFORMS` (defaults to `x86|x64|arm64`)

Run the **Publish JitHub to Microsoft Store** workflow manually and provide a four-part `release_version` such as `1.6.5.0`. The workflow first checks out `nerocui/jithub-vs-code`, builds the editor assets into `artifacts\EditorAssets\dist`, patches `JitHub.WinUI\Package.appxmanifest` at runtime from the configured environment values, builds a Store upload package, uploads the build artifacts, and then publishes the generated `.appxupload` or `.msixupload` to the Microsoft Store.

The workflow accepts an `editor_assets_ref` input if you ever need to point it at a non-default `jithub-vs-code` ref for testing, but it now defaults to `master`.

If the publish step fails with Partner Center authorization errors, the remaining fix is not in GitHub Actions itself: the linked Microsoft Entra application still needs the correct Partner Center access for the individual developer account.

The Static Web Apps workflow now publishes `JitHub.Web` in Release mode with Blazor WebAssembly AOT before uploading the generated `wwwroot`, and the auth workflow now publishes `JitHub.Auth` as a Native AOT ASP.NET Core app for **Azure Web App for Linux** instead of an Azure Functions host.

## Contributing 🙌

JitHub is an open source project and welcomes contributions from anyone. If you want to contribute, please follow these steps:

1. Fork this repository and clone it to your local machine. 🍴
2. Create a new branch for your feature or bug fix. 🌿
3. Make your changes and commit them with a descriptive message. 💬
4. Push your branch to your forked repository. 🚀
5. Create a pull request from your branch to this repository's main branch. 🙏
6. Wait for feedback or approval. 👍

Please follow the [code of conduct](CODE_OF_CONDUCT.md) and the [coding style guide](CODING_STYLE.md) when contributing.

## License 📄

JitHub is licensed under the MIT License. See [LICENSE](LICENSE) for details.
