# Extensibility API

MarkdownRenderer extensions are parser-independent and declarative. They produce
semantic content; the viewer remains responsible for layout, paint, source maps,
accessibility, and virtualization.

## Register an extension

Implement `IMarkdownExtension` and add it while building an immutable engine:

```csharp
using MarkdownRenderer;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;

public sealed class CalloutExtension : IMarkdownExtension
{
    public string Id => "Contoso.Markdown.Callouts";

    public void Configure(MarkdownExtensionBuilder builder)
    {
        builder
            .AddFeature("contoso-callouts")
            .RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, RenderCallout);
    }

    private static void RenderCallout(
        MarkdownExtensionContext context,
        MarkdownContentBuilder content)
    {
        content.AddHostedElement(
            "Contoso.Markdown.Callout",
            context.Node.SourceSpan,
            MarkdownStyleRole.Quote,
            MarkdownAccessibilityRole.Group,
            new Dictionary<string, string>
            {
                [MarkdownHostedElementAttributes.DesiredHeight] = "72",
                ["label"] = context.Node.Literal ?? string.Empty,
            });
    }
}

using MarkdownEngine engine = new MarkdownEngineBuilder()
    .UseExtension(new CalloutExtension())
    .Build();
```

Every ordinary engine is disposable, including engines with no optional owned
service. `MarkdownEngine.Default` and the built-in GFM/GitHub shared engines are
the only non-poisonable process-wide instances.

`UseExtension()` borrows the supplied extension and anything captured by its
callbacks; the engine does not dispose those objects. A stateful extension pack
whose service lifetime should follow each engine must instead register a public
owned factory:

```csharp
builder.UseOwnedExtensionFactory(
    "Contoso.Markdown.Callouts",
    () =>
    {
        IDisposable service = CreateCalloutService();
        IMarkdownExtension extension = CreateCalloutExtension(service);
        return new MarkdownOwnedExtension(extension, service);
    });
```

The factory must create a fresh pair on every invocation, and the returned
extension's `Id` must exactly match the registered identifier. A successful
`Build()` transfers the resource to that engine. Disposal first cancels and
retires active extension callbacks and their cancellation registrations, then
disposes the resource; failed binding also disposes it. The reusable builder or
frozen `MarkdownExtensionSet` retains only the factory, so `Build()`,
`ToBuilder()`, and `UseExtensions()` each receive independent resources. A
factory can be invoked concurrently when the same frozen set is used by
independent builders, so it must not require a UI thread and must be thread-safe.
Factory-created extensions may include another frozen set containing owned
factories. Those nested factories are expanded iteratively in declaration order,
receive independent resources, and are rejected deterministically on cycles or
duplicate identifiers.

`MarkdownExtensionSet` is frozen when the engine is built. Registration uses an
exact syntax-kind string and is deterministic and AOT-friendly. Use
`MarkdownSyntaxKinds` for syntax produced by the built-in parser. Extension code
sees immutable `MarkdownSyntaxNode` values rather than Markdig or viewer-layout
objects.

Use `RegisterBlockAsync` or `RegisterInlineAsync` when producing content requires
asynchronous work. `ParseAsync` awaits these callbacks without blocking its parse
worker. Both synchronous and asynchronous callbacks can run concurrently for
different documents parsed by the same engine. Callback implementations and all
captured services must therefore be thread-safe; immutability of the registration
set does not make arbitrary callback state thread-safe.

`TryGetBlockRenderer` and `TryGetInlineRenderer` expose only pipelines made
entirely from synchronous registrations. They return `false` when any handler
for that syntax kind is asynchronous; use the corresponding `TryGetAsync...`
lookup instead. The API never converts an asynchronous callback into a blocking
delegate. Pipelines that capture engine-owned resources are lifetime-leased even
when obtained through these public lookup methods: an invocation already in
progress delays resource disposal, while a new invocation after engine disposal
throws `ObjectDisposedException` before entering extension code.

## Declarative output

`MarkdownContentBuilder` defines semantic text, container, link, list, table,
code, image, hosted-element, and package-qualified custom primitives. Every item
carries a half-open UTF-16 `SourceSpan` and can carry a `MarkdownStyleRole`,
accessibility role, and immutable attributes.

Source ranges identify text rather than syntax-node identity, so nested nodes
can share a range. `MarkdownDocument.GetBlockExtensionContent()` and
`GetInlineExtensionContent()` return all matching fragments in parsed-node
order; the `TryGet...` conveniences return the first match.

The WinUI adapter consumes block and inline declarative output: text, code,
images, links, lists, tables, containers, hosted elements and registered custom
scene primitives. Unsupported fragments fall back atomically to built-in
rendering. Pack-specific syntax still requires a corresponding parser adapter;
an arbitrary custom key alone does not create a renderer.

Extension renderers should:

- honor `MarkdownExtensionContext.CancellationToken`;
- avoid sync-over-async blocking; register asynchronous renderers for asynchronous work;
- make captured state safe for concurrent document parses;
- preserve accurate source spans;
- emit accessibility roles deliberately;
- avoid UI-thread state and mutable global registration;
- use stable, package-qualified feature, syntax, and factory keys.

## Hosted WinUI elements

A declarative extension can emit `MarkdownContentKind.HostedElement` through
`MarkdownContentBuilder.AddHostedElement()`. The view resolves the request near
the effective viewport through `IMarkdownHostedElementFactory`:

```csharp
public sealed class WidgetFactory : IMarkdownHostedElementFactory
{
    public ValueTask<FrameworkElement?> CreateAsync(
        MarkdownHostedElementRequest request,
        CancellationToken cancellationToken)
    {
        if (request.FactoryKey != "Contoso.Markdown.Widget")
            return ValueTask.FromResult<FrameworkElement?>(null);

        return ValueTask.FromResult<FrameworkElement?>(
            new Button { Content = request.Attributes["label"] });
    }

    public void Recycle(
        MarkdownHostedElementRequest request,
        FrameworkElement element)
    {
        // Detach app-owned handlers or release app-owned resources.
    }
}

view.HostedElementFactory = new WidgetFactory();
```

The factory receives only immutable request data: factory key, source range,
layout bounds, effective viewport, and attributes. Creation and recycling occur
on the UI thread as elements enter and leave the realization band.

Use hosted elements for genuinely interactive native UI. Prefer declarative
painted content for large repeated sets or content that must participate deeply
in selection and semantic text.

### Vector paint roles

Vector scenes can keep an explicit authored palette or resolve colors from the
host theme at draw time. The original `MarkdownVectorPaintStyle` constructor is
backward compatible: non-null colors are `Authored`, while null colors retain
their semantic-foreground behavior. Use the semantic factory when a scene
should adapt without being reparsed:

```csharp
var nodeStyle = MarkdownVectorPaintStyle.CreateSemantic(
    MarkdownVectorPaintRole.Surface,
    MarkdownVectorPaintRole.Foreground,
    fillArgb: 0xFFF4F4F4,
    strokeArgb: 0xFF202020,
    strokeWidth: 1);
```

`Foreground`, `Surface`, and `Link` resolve from the current renderer palette
in Light, Dark, and High Contrast themes. `Authored` preserves its non-null ARGB
value in Light and Dark. Windows High Contrast always maps visible authored
paint to a suitable system surface, foreground, or link color so text and
geometry remain distinguishable.

Vector text has an independent font-family contract. The original
`MarkdownVectorTextStyle` constructor keeps its scene-authored family. Use the
`MarkdownVectorFontRole.Host` overload when text should use the containing
markdown role's resolved `FontFamily` (for example, `Diagram.FontFamily`):

```csharp
var labelStyle = new MarkdownVectorTextStyle(
    MarkdownVectorFontRole.Host,
    fontFamily: "Segoe UI", // deterministic fallback when no host family is available
    fontSize: 16);
```

Font size remains geometric because vector commands are already positioned in
scene coordinates. A positive `MarkdownVectorScene.ReferenceFontSize` is the
scene size that maps to the resolved markdown role font size. When it is zero,
the block renderer infers a text-length-weighted reference from `DrawText`
commands; a scene with no text falls back to the renderer text-scale factor.
This lets `Diagram.FontSize` and 100/150/200% text scaling resize the complete
diagram without distorting its internal layout.

## Feature packs

The official GFM, GitHub, HTML, Math, Mermaid, ThorVG, and TextMate packages use
the same opt-in packaging principle. Their presence must not be inferred from the
lean base package. Math supplies CSharpMath-based vector typesetting; Mermaid
supplies a selected-RID Merman engine with validated MMIR scene output. Both
retain bounded processing and explicit fallback behavior.
