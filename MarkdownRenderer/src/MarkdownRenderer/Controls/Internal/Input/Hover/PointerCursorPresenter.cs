using System;
using Microsoft.UI.Input;

namespace MarkdownRenderer.Controls.Internal.Input.Hover;

/// <summary>
/// Sets the renderer's pointer cursor to the hand over links, the I-beam over
/// selectable text, or no override.
/// </summary>
/// <remarks>
/// No override matters: with the renderer's cursor cleared, hosted embeds such
/// as buttons and check boxes show their own cursors instead of inheriting an
/// I-beam. The two system cursors are cached so a hand/I-beam transition does
/// not create a new disposable object on every pointer move.
/// </remarks>
internal sealed class PointerCursorPresenter
{
    private readonly Action<InputCursor?> _applyCursor;
    private InputSystemCursor? _hand;
    private InputSystemCursor? _iBeam;

    // The shape last applied, or null after a reset to no override.
    private InputSystemCursorShape? _currentShape;

    /// <param name="applyCursor">
    /// Sets the owning element's cursor. UIElement.ProtectedCursor is only
    /// settable by the element itself, so the owner supplies the setter.
    /// </param>
    public PointerCursorPresenter(Action<InputCursor?> applyCursor)
    {
        _applyCursor = applyCursor;
    }

    /// <summary>
    /// Applies <paramref name="shape"/>, or clears the override when it is
    /// null. Does nothing when the shape is unchanged.
    /// </summary>
    public void SetShape(InputSystemCursorShape? shape)
    {
        if (shape == _currentShape) return;
        // Record the intent first so a setter that keeps throwing is not
        // retried on every pointer move.
        _currentShape = shape;
        try
        {
            _applyCursor(shape switch
            {
                InputSystemCursorShape.Hand =>
                    _hand ??= InputSystemCursor.Create(InputSystemCursorShape.Hand),
                InputSystemCursorShape.IBeam =>
                    _iBeam ??= InputSystemCursor.Create(InputSystemCursorShape.IBeam),
                _ => null
            });
        }
        catch { /* ProtectedCursor isn't always settable */ }
    }

    /// <summary>
    /// Clears the override and disposes the cached cursors. They are created
    /// again on next use.
    /// </summary>
    public void Release()
    {
        // Clear the element's cursor before disposing the native cursor
        // objects so the compositor never reads a dangling handle in the
        // same frame.
        _applyCursor(null);
        _currentShape = null;
        _hand?.Dispose();
        _iBeam?.Dispose();
        _hand = null;
        _iBeam = null;
    }
}
