<p align="center">
  <span><img src="JitHub/Assets/JitHubLogo.png" alt="JitHub Logo" width="96" height="96">
  <h1 align="center">JitHub</h1></span>
</p>



<p align="center">
  JitHub is the ultimate UWP app for GitHub lovers 💖. It lets you browse GitHub repositories, issues, pull requests, and more on your Windows device. It is designed to be smooth, fluid, and native, using WinUI for the look and feel and optimized for touch. JitHub is fast, beautiful, and powerful 💪.
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

To build JitHub from source, you need Visual Studio 2019 with the following workloads:

- Universal Windows Platform development 🖥️

JitHub is powered by the following NuGet packages:

- Microsoft.UI.Xaml 🎨
- Octokit 🐙
- Markdig 📑
- Html Agility Pack 🕸️
- Skiasharp ✏️
- Microsoft.Toolkit.Uwp ⚙️

Then, you need to create a file named `appsettings.json` in the `JitHub` project folder with the following content. Go to [settings > Developer settings](https://github.com/settings/developers) and create an OAuth app. Name it however you like, and in there you can get your clientID and secret. Copy them into the `appsettings.json` file. This file is gitignored, so please never commit it.
```json
{
  "Credential": {
    "ClientId": "<your client ID>"
  }
}
```

Finally, set environment variable `JithubAppSecret` to your GitHub seret and `JitHubClientId` to your GitHub client ID.

Now you need to download the built vs code files. Run `.\download-vscode.ps1` in PowerShell. This script will download the latest release of vs code from [jithub-vs-code](https://github.com/nerocui/jithub-vs-code) and unzip it to the `JitHub/Assets/dist` folder. No additional action needs to be performed.

After that, you can open the `JitHub.sln` file in Visual Studio and run the app.

### Microsoft Store release workflow

The repository now includes a manual GitHub Actions workflow at `.github/workflows/jithub-store-release.yml` for building a Store upload package and publishing it to Partner Center in one run.

JitHub is currently published from an **individual** Partner Center account. Full automation is still possible, but Microsoft Store publishing uses the same Microsoft Entra application flow that organization accounts use. That means the GitHub environment must contain the Partner Center product identity values **and** a working Entra tenant/app registration that has been granted access in Partner Center.

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
- `JITHUB_STORE_BUNDLE_PLATFORMS` (defaults to `x86|x64|arm64`)

Run the **Publish JitHub to Microsoft Store** workflow manually and provide a four-part `release_version` such as `1.6.5.0`. Leave `use_signing_certificate` set to `false` unless `STORE_PACKAGE_CERTIFICATE_BASE64` and `STORE_PACKAGE_CERTIFICATE_PASSWORD` are configured in the `microsoft-store` environment. The workflow patches `JitHub\Package.appxmanifest` at runtime from the configured environment values, builds a Store upload package, uploads the build artifacts, and then publishes the generated `.appxupload` or `.msixupload` to the Microsoft Store.

If the publish step fails with Partner Center authorization errors, the remaining fix is not in GitHub Actions itself: the linked Microsoft Entra application still needs the correct Partner Center access for the individual developer account.

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
