namespace MarkdownRenderer.Hosting;

/// <summary>
/// Shared justification for the feature extension classes that suppress the
/// public-API overload rules RS0026 and RS0027.
/// </summary>
/// <remarks>
/// The rules treat every same-named method in one static class as one overload
/// set. A feature's <c>Use*</c> methods extend different receivers (the engine
/// builder, the control builder and each view), so methods for different
/// receivers never compete in overload resolution. The rules' guarantee still
/// holds for each receiver, and a class may suppress them only while it does.
/// </remarks>
internal static class MarkdownExtensionOverloads
{
    internal const string Justification =
        "Each overload extends a different receiver, so overloads for different receivers never compete. " +
        "For each receiver, at most one overload takes optional parameters, and it is that receiver's longest overload.";
}
