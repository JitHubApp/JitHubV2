namespace MarkdownRenderer.Tests.Engineering.PublicXmlDocumentation;

/// <summary>
/// Members of every accessibility, including nested types, whose XML
/// documentation IDs FilterPublicXmlDocumentationTests passes to the filter
/// script to check that only the public surface is kept.
/// </summary>
public class PublicXmlDocumentationVisibilityFixture
{
    public int PublicField = 1;
    protected int ProtectedField = 2;
    protected internal int ProtectedInternalField = 3;
    internal int InternalField = 4;
    private int _privateField = 5;

    public int ReadFields() => PublicField + ProtectedField + ProtectedInternalField + InternalField + _privateField;

    public void PublicMethod() { }
    protected void ProtectedMethod() { }
    protected internal void ProtectedInternalMethod() { }
    internal void InternalMethod() { }
    private void PrivateMethod() { }

    public int PublicProperty { get; private set; }
    protected int ProtectedProperty { get; set; }
    protected internal int ProtectedInternalProperty { get; set; }
    internal int InternalProperty { get; set; }
    private int PrivateProperty { get; set; }

    public event EventHandler? PublicEvent { add { } remove { } }
    protected event EventHandler? ProtectedEvent { add { } remove { } }
    protected internal event EventHandler? ProtectedInternalEvent { add { } remove { } }
    internal event EventHandler? InternalEvent { add { } remove { } }
    private event EventHandler? PrivateEvent { add { } remove { } }

    public int this[int index, string key] => index + key.Length;

    public class PublicNested
    {
        public void VisibleMethod() { }
    }

    protected class ProtectedNested { }
    protected internal class ProtectedInternalNested { }

    internal class InternalNested
    {
        public void HiddenMethod() { }
    }

    private class PrivateNested
    {
        public void HiddenMethod() { }
    }
}
