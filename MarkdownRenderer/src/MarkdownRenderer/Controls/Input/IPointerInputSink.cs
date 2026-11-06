using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Receives the pointer events of renderer chrome, such as the touch
/// selection handles and the drag shield, so they share the canvas's
/// ordered input path.
/// </summary>
internal interface IPointerInputSink
{
    /// <summary>Routes a raw pointer event through the renderer's input queue.</summary>
    void DispatchPointerInput(PointerInputKind kind, object sender, PointerRoutedEventArgs args);

    /// <summary>
    /// Routes a recognized gesture at a canvas point through the renderer's
    /// input queue. Returns true when the gesture was handled.
    /// </summary>
    bool DispatchRecognizedGesture(PointerInputKind kind, Point point, PointerDeviceType device);
}
