<p align="center">
  <span><img src="JitHub/Assets/JitHubLogo.png" alt="JitHub Logo" width="96" height="96">
  <h1 align="center">JitHub</h1></span>
</p>
<p align="center">
  <a title="GitHub Releases" target="_blank" href="https://github.com/nerocui/JitHubV2/releases">
    <img src="https://img.shields.io/github/v/release/nerocui/JitHubV2?include_prereleases" alt="Release" />
  </a>
  <a title="Repo Size" target="_blank" href="https://github.com/nerocui/JitHubV2/releases">
    <img src="https://img.shields.io/github/repo-size/nerocui/JitHubV2?color=%23cc0000" alt="Release" />
  </a>
</p>
<p align="center">
  <a title="Microsoft Store" target="_blank" href="https://apps.microsoft.com/store/detail/jithub/9MXRBJBB552V">
  <a style="text-decoration:none" href="https://apps.microsoft.com/store/detail/9MXRBJBB552V?launch=true&mode=full">
    <picture>
      <source media="(prefers-color-scheme: light)" srcset="https://get.microsoft.com/images/en-us%20dark.svg" width="200" />
      <img src="https://get.microsoft.com/images/en-us%20light.svg" width="200" />
    </picture></a>
</p>

A fast, fluent and free GitHub client for Windows, designed for GitHub lovers 💖 who want a bit of spice with their setup.

- **Search:** browse GitHub repositories by topics, languages, or keywords 🔎
- **Browse:** view repository details, files, commits, issues, pull requests, and actions 📝
- **Perform actions:** star, fork, watch, or clone any repository ⭐
- **Multitask:** create, edit, or close issues and pull requests ✅
- **Interact:** comment on issues and pull requests with Markdown support 🗣️
- **Work:** Manage your notifications and profile 🔔
- **Style:** Switch between light and dark themes 🌞🌙

## 📸 Screenshots

<a title="JitHub Screenshot" target="_blank" href="https://github.com/nerocui/JitHubV2">
  <img align="center" src="https://github.com/user-attachments/assets/54fbb757-0f13-4f7d-895d-b04aca274a0c" alt="Screenshot" />
</a>

---

<a title="JitHub Screenshot" target="_blank" href="https://github.com/nerocui/JitHubV2">
  <img align="center" src="https://github.com/user-attachments/assets/25a1112c-0677-454d-9bd6-d70118718870" alt="Screenshot" />
</a>

## 🦜 Contributing & Feedback

There are multiple ways to participate in the community:

- Upvote popular feature requests
- [Submit a new feature](https://github.com/nerocui/JitHubV2/pulls)
- [File bugs and feature requests](https://github.com/nerocui/JitHubV2/new/choose).
- Review source [code changes](https://github.com/nerocui/JitHubV2/commits)

JitHub is an open source project and welcomes source code contributions from anyone. If you want to contribute, please follow these steps:
1. Fork this repository and clone it to your local machine. 🍴
2. Create a new branch for your feature or bug fix. 🌿
3. Make your changes and commit them with a descriptive message. 💬
4. Push your branch to your forked repository. 🚀
5. Create a pull request from your branch to this repository's main branch. 🙏
6. Wait for feedback or approval. 👍

Please follow the [code of conduct](CODE_OF_CONDUCT.md) and the [coding style guide](CODING_STYLE.md) when contributing.

### 🏗️ Codebase Structure

```
.
├──JitHub                               // JitHub app code (such as code related to UI and GitHub's API)
├──JitHub.VSCode.Client                 // JitHub VSCode implementation
├──JitHub.Controls.Editor               // JitHub file editor
├──JitHub.Services.GitHub.Contributions // JitHub client to obtain user contribution details
├──JitHub.Utilities.SVG                 // JitHub SVG image renderer service
|
├──System.Text.Json.Viewer              // JitHub Json viewer service (for credentials)
├──Utilities.Common                     // JitHub utilities common files
|
├──ScreenShots                          // JitHub screenshots for README and other reference uses.
|
├──Markdig.Client.Markdig               // Markdig markdown viewer files
└──Markdig.UWP                          // Markdig implementation for UWP
```

## 🔨 Building the Code

### 1️⃣ Prerequisites

Ensure you have following components:

- [Git](https://git-scm.com/)
- [Visual Studio 2022](https://visualstudio.microsoft.com/vs/) with following individual components:
  - Universal Windows Platform Software Development Kit
- [Windows 11 or Windows 10](https://www.microsoft.com/en-us/windows) (version 1809+)
- JitHub is also powered by the following awesome NuGet packages:
  - [`Microsoft.UI.Xaml`](https://www.nuget.org/packages/Microsoft.UI.Xaml) 🎨
  - [`Octokit`](https://www.nuget.org/packages/Octokit) 🐙
  - [`Markdig`](https://www.nuget.org/packages/Markdig) 📑
  - [`HtmlAgilityPack`](https://www.nuget.org/packages/HtmlAgilityPack/) 🕸️
  - [`SkiaSharp`](https://www.nuget.org/packages/Skiasharp) ✏️
  - [`Microsoft.Toolkit.Uwp`](https://www.nuget.org/packages/Microsoft.Toolkit.Uwp) ⚙️

### 2️⃣ Git

Clone the repository:

```git
git clone https://github.com/nerocui/JitHubV2
```

### 3️⃣ Build the project

- Open `JitHub.sln`.
- Make sure all projects are loaded.
- Create a GitHub OAuth app and put the `client-id` and `secret-key` inside the `appsettings.json` file in the `JitHub` project directory.

> [!CAUTION]
> The `appsettings.json` file should be ignored automatically by Git. If for any reason it is not, _do not_ commit it.

```json
{
  "Credential": {
    "ClientId": "<your client ID>"
  }
}
```

> [!TIP]
> See the [docs](https://github.com/nerocui/JitHubV2/wiki) on how to create a GitHub OAuth app.

Finally, set environment variable `JithubAppSecret` to your GitHub seret and `JitHubClientId` to your GitHub client ID.

Now you need to download the built vs code files. Run `.\download-vscode.ps1` in PowerShell. This script will download the latest release of vs code from [jithub-vs-code](https://github.com/nerocui/jithub-vs-code) and unzip it to the `JitHub/Assets/dist` folder. No additional action needs to be performed.

After that, you can open the `JitHub.sln` file in Visual Studio and run the app.

- Set the Startup Project to `JitHub`
- Build with `DEBUG|x64` (or `DEBUG|Any CPU`)

## ⚖️ License

Copyright (c) 2023 - present `nerocui`

Licensed under the MIT license as stated in the [LICENSE](LICENSE.md).
