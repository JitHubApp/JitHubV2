# Native integration and hosted controls

Declarative extensions may request a real WinUI element without coupling the
extension to the viewer's private layout representation.

## Request and factory

An extension emits a hosted element with
`MarkdownContentBuilder.AddHostedElement()`. The request names a stable factory
key and contains only immutable attributes and a UTF-16 source span. Near the
effective viewport, the view calls the host's `IMarkdownHostedElementFactory`.

```csharp
public sealed class AppElementFactory : IMarkdownHostedElementFactory
{
    public ValueTask<FrameworkElement?> CreateAsync(
        MarkdownHostedElementRequest request,
        CancellationToken cancellationToken)
    {
        return request.FactoryKey == "Contoso.IssueCard"
            ? ValueTask.FromResult<FrameworkElement?>(CreateIssueCard(request))
            : ValueTask.FromResult<FrameworkElement?>(null);
    }

    public void Recycle(
        MarkdownHostedElementRequest request,
        FrameworkElement element)
    {
        // Detach app-owned handlers and release app-owned state.
    }
}

view.HostedElementFactory = new AppElementFactory();
```

`MarkdownHostedElementRequest` exposes the factory key, source range, layout
bounds, effective viewport, and immutable attributes. It does not expose parser
or layout implementation objects.

## Lifecycle and interaction

- `CreateAsync` runs on the UI thread and is cancellation-aware.
- The view realizes elements near the effective viewport and may recycle them as
  they move away.
- Factories must not assume a single element instance lives for the document's
  lifetime.
- A `null` result, factory exception, or element-attachment failure causes that
  source block to be rebuilt with the native Markdown renderer. The failed
  request is not retried on every viewport pass; changing the source, document,
  engine, parser, or hosted-element factory permits a fresh attempt.
- Normal pointer, focus, and UI Automation behavior belongs to the hosted WinUI
  element.
- The viewer coordinates selection and document focus around realized elements.

Hosted elements suit buttons, app cards, and other genuinely interactive native
UI. The current preview's viewer adapter handles block-level hosted elements and
containers composed only of hosted elements. Static or repeated declarative
text, links, lists, tables, code, images, inline content, and custom primitives
still use the built-in-renderer fallback until their native adapters are
implemented.
