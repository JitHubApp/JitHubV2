namespace MarkdownRenderer.Tests;

/// <summary>
/// Overloaded and generic members whose XML documentation IDs
/// FilterPublicXmlDocumentationTests uses to check that the filter script
/// matches each signature exactly.
/// </summary>
public class PublicXmlDocumentationSignatureFixture
{
    public void Overloaded(int value) { }
    private void Overloaded(string value) { }

    public void Generic<T>(IEnumerable<T> values) { }
    private void Generic<T>(string value) { }

    public void NestedGeneric(Dictionary<string, List<int>> values) { }
    public void RefValue(ref int value) { }
    public void Rectangular(int[,] values) { }

    public int this[int index, string key] => index + key.Length;
}
