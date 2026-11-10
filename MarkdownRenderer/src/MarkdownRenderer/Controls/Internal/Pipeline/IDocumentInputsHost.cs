using System.Threading.Tasks;
using MarkdownRenderer.Document;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Internal.Pipeline;

/// <summary>What the document inputs need from the control and the owners they reset.</summary>
internal interface IDocumentInputsHost
{
    /// <summary>The control's Markdown property.</summary>
    string? Markdown { get; }

    /// <summary>The control's Document property.</summary>
    MarkdownDocument? Document { get; }

    /// <summary>The rebuild pipeline running now, if any.</summary>
    Task? ActivePipeline { get; }

    /// <summary>Assigns the control's Engine property. Its change callback runs before this returns.</summary>
    void AssignEngine(MarkdownEngine engine);

    /// <summary>Clears the control's Document property. Its change callback runs before this returns.</summary>
    void ClearDocument();

    /// <summary>Sets the control's Document property. Its change callback runs before this returns.</summary>
    void SetDocument(MarkdownDocument document);

    /// <summary>Ends pointer, selection and focus interaction with the document being replaced.</summary>
    void ResetInputForDocumentReplacement();

    /// <summary>Forgets hosted element fallbacks and cached code highlighting of the old document.</summary>
    void ClearDocumentCaches();

    void RequestRebuild(RebuildReason reason);

    void RaiseDisclosureToggled(bool expanded);
}
