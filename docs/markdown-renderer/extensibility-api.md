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

The GFM project provides a convenience extension:

```csharp
registry.UseGitHubFlavoredMarkdown();
```

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

## Choosing renderer vs embed

| Need | Prefer |
| --- | --- |
| Static native drawing | Custom `BlockBox` renderer |
| Text selection and source mapping | Custom box/run with source spans |
| Real button/focus/input behavior | `IMarkdownEmbedFactory` |
| UIA peer already exists in WinUI | Hosted control |
| Thousands of repeated lightweight visuals | Painted box, not hosted controls |

## Extension maturity gaps

- Public XML docs are incomplete.
- `MarkdownLayoutContext` and layout boxes are low-level for third-party authors.
- There is no high-level builder/facade yet.
- Custom renderer samples need to be added.
- Extension versioning and compatibility policy need to be defined before NuGet
  publication.

