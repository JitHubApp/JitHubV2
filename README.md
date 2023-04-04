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

<img src="https://github.com/nerocui/JitHub/blob/dev-feature/ScreenShots/screenshot1.png" width="640"/>
<img src="https://github.com/nerocui/JitHub/blob/dev-feature/ScreenShots/screenshot2.png" width="640"/>
<img src="https://github.com/nerocui/JitHub/blob/dev-feature/ScreenShots/screenshot3.png" width="640"/>

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
