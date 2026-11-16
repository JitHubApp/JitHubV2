namespace MarkdownRenderer.Tests.Engineering.PublicXmlDocumentation;

/// <summary>
/// Generic and non-generic nested types whose XML documentation IDs
/// FilterPublicXmlDocumentationTests uses to check that the filter script
/// resolves signatures naming nested types of a generic type.
/// </summary>
public class PublicXmlDocumentationNestedTypeFixture<T>
{
    public class Inner<U> { }

    public class NonGenericInner { }

    public void AcceptNested(Inner<string> value) { }

    public void AcceptNonGenericNested(NonGenericInner value) { }
}
