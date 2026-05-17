# Extensibility API

The renderer has two main extension points:

1. Markdig pipeline and node renderers;
2. hosted WinUI embed factories.

## MarkdownExtensionRegistry

`MarkdownExtensionRegistry` lets consumers configure Markdig and register native
renderers for concrete AST node types.

```csharp
var registry = new MarkdownExtensionRegistry()
    .ConfigurePipeline(p => p.UsePipeTables())
    .RegisterRenderer(new MyTableRenderer());
```

Renderer lookup is exact-type only. Base type and interface matching are not
performed. This keeps dispatch O(1), predictable, and AOT-safe.

## Custom node renderer

```csharp
public sealed class MyBlockRenderer : MarkdownNodeRenderer<MyBlock>
{
    public override BlockBox? BuildBlock(MyBlock node, MarkdownLayoutContext context)
    {
        // Build and return a BlockBox.
    }
}
```

Custom renderers produce native layout boxes. They are responsible for source
mapping, block indices, styles, and accessibility implications when applicable.
This is an advanced extension-author API: `BuildBlock` runs during background
layout, so renderer implementations must not touch WinUI objects or dispatcher
affine state.

## Pipeline customization

Use `ConfigurePipeline` to enable additional Markdig extensions:

```csharp
registry.ConfigurePipeline(p =>
{
    p.UsePipeTables();
    p.UseTaskLists();
    p.UseAutoLinks();
});
```

The GFM package provides convenience helpers:

```csharp
var control = GfmMarkdownRenderer.CreateDefault(markdownSource);

var control2 = new MarkdownRendererControlBuilder()
    .UseGitHubFlavoredMarkdown()
    .UseMarkdownExtra()
    .WithMarkdown(markdownSource)
    .Build();
```

`UseGitHubFlavoredMarkdown()` intentionally stays strict to GFM. Add
`UseMarkdownExtra()` when an app also wants definition lists, abbreviations, and
figures. Raw HTML and LaTeX/math are not enabled by either helper.

## Embed factories

Use `IMarkdownEmbedFactory` when the output should be a real WinUI control rather
than a painted box.

Best fit:

- buttons;
- checkboxes;
- cards;
- app-specific issue/PR/user pills;
- interactive media blocks;
- custom forms.

Not a good fit:

- simple styled text;
- static icons that can be painted;
- controls that require expensive measurement on the UI thread.

## Mermaid and diagram samples

The 1.0 renderer does not bundle a diagram engine or JavaScript sandbox. Use the
embed API to wire the renderer your app already trusts:

```csharp
public sealed class MermaidEmbedFactory : IMarkdownEmbedFactory
{
    public bool CanCreate(Block block)
        => block is FencedCodeBlock fenced &&
           string.Equals(fenced.Info, "mermaid", StringComparison.OrdinalIgnoreCase);

    public float MeasureHeight(Block block, float availableWidth) => 240;

    public FrameworkElement CreateBlock(Block block)
    {
        var fenced = (FencedCodeBlock)block;
        var source = fenced.Lines.ToString();
        return new TextBlock { Text = source, TextWrapping = TextWrapping.Wrap };
    }
}
```

Keep parsing and measurement cheap and WinUI-free. Do expensive rendering or
control creation from `CreateBlock`, which runs on the UI thread and participates
in viewport realization.

## Choosing renderer vs embed

| Need | Prefer |
| --- | --- |
| Static native drawing | Custom `BlockBox` renderer |
| Text selection and source mapping | Custom box/run with source spans |
| Real button/focus/input behavior | `IMarkdownEmbedFactory` |
| UIA peer already exists in WinUI | Hosted control |
| Thousands of repeated lightweight visuals | Painted box, not hosted controls |

## Versioning

The package is pre-1.0, so source-breaking cleanup is allowed when it removes
accidental public internals or stabilizes the extension boundary. At 1.0 and
later, public APIs follow semantic versioning and breaking changes require a
major version.
