<!--# <img width="64" align="center" src="JitHub/Assets/JitHubLogo.png" /> JitHub

#### A fast, fluent and free GitHub client for Windows.

<p align="center">
  <a title="GitHub Releases" target="_blank" href="https://github.com/nerocui/JitHubV2/releases">
    <img align="left" src="https://img.shields.io/github/v/release/nerocui/JitHubV2?include_prereleases" alt="Release" />
  </a>
  <a title="GitHub Releases" target="_blank" href="https://github.com/nerocui/JitHubV2/releases">
    <img align="left" src="https://img.shields.io/github/repo-size/nerocui/JitHubV2?color=%23cc0000" alt="Release" />
  </a>
</p>

<br/>

---

## 🎁 Installation

### 🪟 Microsoft Store
###### ⭐Recommended⭐

<a title="Microsoft Store" href="https://apps.microsoft.com/store/detail/jithub/9MXRBJBB552V">
  <img src="https://user-images.githubusercontent.com/71598437/229349655-404beb46-01fa-494c-aba7-5ed94344d9a6.png" alt="Release" />
</a>

### 😺 GitHub

<a title="GitHub" href='#-building-the-code'>
  <img src='https://user-images.githubusercontent.com/74561130/160255105-5e32f911-574f-4cc4-b90b-8769099086e4.png'alt='Get it from GitHub' />
</a>

### 📸 Screenshots

<a title="Emerald Screenshot" target="_blank" href="https://github.com/RiversideValley/Emerald">
  <img align="left" src="https://user-images.githubusercontent.com/71598437/212673147-54e79843-76aa-44ff-9db3-60b025334f07.png" alt="Release" />
</a>

###### 📝 This screenshot is from [`redesign`](https://github.com/RiversideValley/Emerald/pull/19)

## 🦜 Contributing & Feedback

There are multiple ways to participate in the community:

- Upvote popular feature requests
- [Submit a new feature](https://github.com/RiversideValley/Emerald/pulls)
- [File bugs and feature requests](https://github.com/RiversideValley/Emerald/issues/new/choose).
- Review source [code changes](https://github.com/RiversideValley/Emerald/commits)

### 🏗️ Codebase Structure

```
.
├──Emerald.App                       // Emerald app code and packager
|  ├──Emerald.App                    // Emerald app code (such as code related to UI but not Minecraft)
|  └──Emerald.App.Package            // Package code for generating an uploadable MSIX bundle.
└──Emerald.Core                      // Emerald core code (such as code related to launching and modifying Minecraft
```

### 🗃️ Contributors

<a href="https://github.com/RiversideValley/Emerald/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=RiversideValley/Emerald" />
</a>

## 🔨 Building the Code

### 1️⃣ Prerequisites

Ensure you have following components:

- [Git](https://git-scm.com/)
- [Visual Studio 2022](https://visualstudio.microsoft.com/vs/) with following individual components:
  - Universal Windows Platform Software Development Kit
  - .NET 7
  - Windows App Software Development Kit
  - Windows 11 SDK
- [Windows 11 or Windows 10](https://www.microsoft.com/en-us/windows) (version 1809+)
- At least 4gb of RAM
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)

### 2️⃣ Git

Clone the repository:

```git
git clone https://github.com/RiversideValley/Emerald
```
(`redesign` is the latest branch)

### 3️⃣ Build the project

- Open `Emerald.sln`.
- Set the Startup Project to `Emerald.Package`
- Build with `DEBUG|x64` (or `DEBUG|Any CPU`)

## ⚖️ License

Copyright (c) 2022-2023 Depth

Licensed under the Nightshade Vexillum license as stated in the [LICENSE](LICENSE.md).
---

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

<img src="https://github.com/nerocui/JitHubV2/blob/main/ScreenShots/screenshot1.png" width="640"/>
<img src="https://github.com/nerocui/JitHubV2/blob/main/ScreenShots/screenshot2.png" width="640"/>
<img src="https://github.com/nerocui/JitHubV2/blob/main/ScreenShots/screenshot3.png" width="640"/>

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

Then, you need to create a file named `secrets.json` in the `JitHub` project folder with the following content:
```json
{
  "Credential": {
    "ClientId": "<your client ID>",
    "ClientSecret": "<your client secret>"
  }
}
```
After that, you can open the `JitHub.sln` file in Visual Studio and run the app.

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
-->
