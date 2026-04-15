This folder is the local staging area for `jithub-vs-code` build output.

- Development: run `.\sync-vscode-assets.ps1` from the repo root to build `jithub-vs-code` and copy its `dist` output here.
- CI/CD: workflows should build `jithub-vs-code` and copy the latest `dist` output here before building `JitHub.WinUI`.
- Source control: `artifacts\EditorAssets\dist` is intentionally gitignored and should not be checked in.
