namespace MarkdownRenderer.GitHub.Tests;

/// <summary>Real GitHub profile README sources that the safe HTML tests replay byte for byte.</summary>
internal static class GitHubProfileReadmeSources
{
    /// <summary>
    /// The massgravel/Microsoft-Activation-Scripts README with CRLF line endings,
    /// tabs and trailing hard-break spaces restored from placeholders.
    /// </summary>
    internal static string CreateMassgraveProfileSource() =>
        MassgraveProfileTemplate
            .Replace("{{TAB}}", "\t", StringComparison.Ordinal)
            .Replace("{{SPACE1}}", " ", StringComparison.Ordinal)
            .Replace("{{SPACE2}}", "  ", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";

    private const string MassgraveProfileTemplate = """
            <p align="center"><img src="https://massgrave.dev/img/logo_small.png" alt="MAS Logo"></p>

            <h1 align="center">Microsoft  Activation  Scripts (MAS)</h1>

            <p align="center">Open-source Windows and Office activator featuring HWID, Ohook, TSforge, and Online KMS activation methods, along with advanced troubleshooting.</p>

            <hr>
            {{SPACE2}}
            ## How to Activate Windows / Office / Extended Security Updates (ESU)?

            ### Method 1 - PowerShell ❤️

            1. Click the **Start Menu**, type `PowerShell`, and open it.

            2. Copy and paste the code below and press **Enter.**{{SPACE2}}
               - For **Windows 8.1, 10 and 11**:
                 ```
                 irm https://get.activated.win | iex
                 ```
            {{TAB}} If the above is blocked (by ISP/DNS), try this (needs updated Windows 10 or 11):{{SPACE2}}
            {{TAB}} ```
            {{TAB}} iex (curl.exe -s --doh-url https://1.1.1.1/dns-query https://get.activated.win | Out-String)
            {{TAB}} ```
            {{TAB}}- **Script not launching? Use the below-listed Method 2.**

            3. In the menu that appears, type the number corresponding to one of the **Green** options.

            ---

            ### Method 2 - Traditional (Windows Vista and later)

            1.   Download the script:
                  *   [**MAS_AIO.cmd**](https://dev.azure.com/massgrave/Microsoft-Activation-Scripts/_apis/git/repositories/Microsoft-Activation-Scripts/items?path=/MAS/All-In-One-Version-KL/MAS_AIO.cmd&download=true) (Direct script)
                  *   [**MAS_AIO.zip**](https://dev.azure.com/massgrave/Microsoft-Activation-Scripts/_apis/git/repositories/Microsoft-Activation-Scripts/items?$format=zip) (If the direct script is blocked by your browser)
            2.   Run the `MAS_AIO.cmd` file.
            3.   In the menu that appears, type the number corresponding to one of the **Green** options.

            ---

            > [!TIP]
            > - Some ISPs/DNS providers block access to our domains. You can bypass this by enabling [DNS-over-HTTPS (DoH)](https://developers.cloudflare.com/1.1.1.1/encryption/dns-over-https/encrypted-dns-browsers/) in your browser.{{SPACE1}}
            > - **Having trouble**? Visit our [troubleshooting page](https://massgrave.dev/troubleshoot) or raise an issue on [GitHub](https://github.com/massgravel/Microsoft-Activation-Scripts/issues).

            > [!NOTE]
            >
            > - The `irm` command in PowerShell downloads a script from a specified URL, and the `iex` command executes it.
            > - Always double-check the URL before executing the command and verify the source is trustworthy when manually downloading files.
            > - Be cautious of third parties spreading malware disguised as MAS by altering the URL in the PowerShell command.

            ---

            <div align="center">
            {{TAB}}
            ### Homepage - [https://massgrave.dev/](https://massgrave.dev/)
            {{SPACE2}}
            [![1.1]][1]
            [![1.2]][2]
            [![1.3]][3]
            [![1.4]][4]
            [![1.5]][5]
            [![1.6]][6]
            [![1.7]][7]

            [1.1]: https://massgrave.dev/img/logo_discord.png (Chat with us without signup)
            [1.2]: https://massgrave.dev/img/logo_reddit.png (Reddit)
            [1.3]: https://massgrave.dev/img/logo_bluesky.png (Bluesky)
            [1.4]: https://massgrave.dev/img/logo_x.png (Twitter)

            [1.5]: https://massgrave.dev/img/logo_github.png (GitHub)
            [1.6]: https://massgrave.dev/img/logo_azuredevops.png (AzureDevOps)
            [1.7]: https://massgrave.dev/img/logo_gitea.png (Self-hosted Git)

            [1]: https://discord.gg/j2yFsV5ZVC
            [2]: https://www.reddit.com/r/MAS_Activator
            [3]: https://bsky.app/profile/massgrave.dev
            [4]: https://twitter.com/massgravel
            [5]: https://github.com/massgravel/Microsoft-Activation-Scripts
            [6]: https://dev.azure.com/massgrave/_git/Microsoft-Activation-Scripts
            [7]: https://git.activated.win/Microsoft-Activation-Scripts

            ---

            Latest Version: 3.12{{SPACE2}}
            Release date: 04-Jul-2026
            """;
}
