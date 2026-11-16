namespace MarkdownRenderer.Theming.Internal.ResourceDictionaries;

internal delegate bool TryGetGraphResource<TNode>(TNode node, string resourceKey, out object value)
    where TNode : class;
