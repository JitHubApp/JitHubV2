# Release checklist

Use this checklist before publishing 1.0 packages.

## Build and test matrix

```powershell
dotnet test MarkdownRenderer\MarkdownRenderer.Tests\MarkdownRenderer.Tests.csproj -c Debug
dotnet test MarkdownRenderer\MarkdownRenderer.PixelTests\MarkdownRenderer.PixelTests.csproj -c Debug

dotnet build MarkdownRenderer\MarkdownRenderer\MarkdownRenderer.csproj -c Debug -p:Platform=x86
dotnet build MarkdownRenderer\MarkdownRenderer\MarkdownRenderer.csproj -c Debug -p:Platform=x64
dotnet build MarkdownRenderer\MarkdownRenderer\MarkdownRenderer.csproj -c Debug -p:Platform=ARM64

dotnet build MarkdownRenderer\MarkdownRenderer.Gfm\MarkdownRenderer.Gfm.csproj -c Debug -p:Platform=x86
dotnet build MarkdownRenderer\MarkdownRenderer.Gfm\MarkdownRenderer.Gfm.csproj -c Debug -p:Platform=x64
dotnet build MarkdownRenderer\MarkdownRenderer.Gfm\MarkdownRenderer.Gfm.csproj -c Debug -p:Platform=ARM64

dotnet build MarkdownRenderer\MarkdownRenderer.Sample\MarkdownRenderer.Sample.csproj -c Debug -p:Platform=x86
dotnet build MarkdownRenderer\MarkdownRenderer.Sample\MarkdownRenderer.Sample.csproj -c Debug -p:Platform=x64
dotnet build MarkdownRenderer\MarkdownRenderer.Sample\MarkdownRenderer.Sample.csproj -c Debug -p:Platform=ARM64

dotnet build MarkdownRenderer\MarkdownRenderer.Sample.Automation\MarkdownRenderer.Sample.Automation.csproj -c Debug -p:Platform=x64
```

Run sample automation against the x64 sample app:

```powershell
dotnet run --project MarkdownRenderer\MarkdownRenderer.Sample.Automation\MarkdownRenderer.Sample.Automation.csproj -c Debug -p:Platform=x64 --no-build -- --app-path MarkdownRenderer\MarkdownRenderer.Sample\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\MarkdownRenderer.Sample.exe
```

## Package commands

```powershell
dotnet pack MarkdownRenderer\MarkdownRenderer\MarkdownRenderer.csproj -c Release -p:Platform=x64
dotnet pack MarkdownRenderer\MarkdownRenderer.Gfm\MarkdownRenderer.Gfm.csproj -c Release -p:Platform=x64
```

## Package inspection

Inspect both packages for:

- package IDs: `MarkdownRenderer` and `MarkdownRenderer.Gfm`;
- author, MIT license expression, repository URL, project URL, tags, README, and icon;
- XML documentation files with `CS1591` enabled;
- symbols/source package expectations for the release channel;
- no accidental public layout/rendering internals in the consumer path;
- ThorVG native assets at `runtimes/win-x86/native`, `runtimes/win-x64/native`, and `runtimes/win-arm64/native`;
- matching PE machine type for each ThorVG asset.

## Manual smoke

Run these on a real Windows machine before publishing:

- Narrator phrasing for headings, lists, tables, links, images, embeds,
  footnotes, definitions, abbreviations, figures, fragments, and copy behavior;
- every built-in Windows contrast theme plus one customized contrast theme;
- light/dark app theme switching during scroll, selection, image/SVG load, and
  hosted-control realization;
- mixed LTR/RTL paragraphs under at least one RTL system language;
- monitor disconnect/reconnect, sleep/resume, or graphics-device-reset smoke;
- x86, x64, and ARM64 sample launch where hardware is available;
- clipboard paste into Notepad, Word, Outlook, browser fields, and at least one
  host app using the control;
- long-document scroll, selection auto-scroll, image load, theme switch, and
  rebuild cancellation under stress.

## Publish rehearsal

- Publish to a private feed or local package source first.
- Install the packages into a clean WinUI app.
- Verify SVG rendering without project-reference paths.
- Verify the GFM helper and builder APIs compile from package references.
- Verify docs links in the package README resolve to the repository docs.
