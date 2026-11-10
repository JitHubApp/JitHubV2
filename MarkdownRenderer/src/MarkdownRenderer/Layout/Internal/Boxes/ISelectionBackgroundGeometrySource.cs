using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Internal.Boxes;

/// <summary>
/// An atomic box whose painted selection background is narrower than its
/// interaction geometry. <see cref="BlockBox.GetSelectionRects"/> keeps the
/// full block for endpoints and hit tests; the adorner paints only these.
/// </summary>
internal interface ISelectionBackgroundGeometrySource
{
    IEnumerable<SelectionBackgroundGeometry> GetSelectionBackgroundGeometry(Document.DocumentRange range);
}
