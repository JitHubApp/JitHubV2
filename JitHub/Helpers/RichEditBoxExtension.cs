using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.Helpers;

public class RichEditBoxExtension
{
    // Standard attached property. It mimics the "Text" property of normal text boxes
    public static readonly DependencyProperty PlainTextProperty =
      DependencyProperty.RegisterAttached("PlainText", typeof(string),
      typeof(RichEditBoxExtension), new PropertyMetadata(null, OnPlainTextChanged));

    // Standard DP infrastructure
    public static string GetPlainText(DependencyObject o)
    {
        return o.GetValue(PlainTextProperty) as string;
    }

    // Standard DP infrastructure
    public static void SetPlainText(DependencyObject o, string s)
    {
        o.SetValue(PlainTextProperty, s);
    }

    private static void OnPlainTextChanged(DependencyObject o,
      DependencyPropertyChangedEventArgs e)
    {
        var source = o as RichEditBox;
        if (o == null || e.NewValue == null)
            return;

        // This attaches an event handler for the TextChange event in the RichEditBox,
        // ensuring that we're made aware of any changes
        AttachRichEditBoxChangingHelper(o);

        // To avoid endless property updates, we make sure we only change the RichText's 
        // Document if the PlainText was modified (vs. if PlainText is responding to 
        // Document being modified)
        var state = GetState(o);
        switch (state)
        {
            case RichEditChangeState.Idle:
                var text = e.NewValue as string;
                SetState(o, RichEditChangeState.PlainTextChanged);
                source.Document.SetText(TextSetOptions.None, text);
                break;

            case RichEditChangeState.RichTextChanged:
                SetState(o, RichEditChangeState.Idle);
                break;

            default:
                Debug.Assert(false, "Unknown state");
                SetState(o, RichEditChangeState.Idle);
                break;
        }
    }

    #region Glue

    // Trivial state machine to determine who last changed the text properties
    enum RichEditChangeState
    {
        Idle,
        RichTextChanged,
        PlainTextChanged,
        Unknown
    }

    // Helper class that just stores a state inside a textbox, determining
    // whether it is already being changed by code or not
    class RichEditChangeStateHelper
    {
        public RichEditChangeState State { get; set; }
    }

    // Private attached property (never seen in XAML or anywhere else) to attach
    // the state variable for us. Because this isn't used in XAML, we don't need
    // the normal GetXXX and SetXXX static methods.
    static readonly DependencyProperty RichEditChangeStateHelperProperty =
      DependencyProperty.RegisterAttached("RichEditChangeStateHelper",
      typeof(RichEditChangeStateHelper), typeof(RichEditBoxExtension), null);

    // Inject our state into the textbox, and also attach an event-handler
    // for the TextChanged event.
    static void AttachRichEditBoxChangingHelper(DependencyObject o)
    {
        if (o.GetValue(RichEditChangeStateHelperProperty) != null)
            return;

        var richEdit = o as RichEditBox;
        var helper = new RichEditChangeStateHelper();
        o.SetValue(RichEditChangeStateHelperProperty, helper);

        richEdit.TextChanged += (sender, args) =>
        {
            // To avoid re-entrancy, make sure we're not already changing
            var state = GetState(o);
            switch (state)
            {
                case RichEditChangeState.Idle:
                    string text = null;
                    richEdit.Document.GetText(TextGetOptions.None, out text);
                    if (text != GetPlainText(o))
                    {
                        SetState(o, RichEditChangeState.RichTextChanged);
                        o.SetValue(PlainTextProperty, text);
                    }
                    break;

                case RichEditChangeState.PlainTextChanged:
                    SetState(o, RichEditChangeState.Idle);
                    break;

                default:
                    Debug.Assert(false, "Unknown state");
                    SetState(o, RichEditChangeState.Idle);
                    break;
            }
        };
    }

    // Helper to set the state managed by the textbox
    static void SetState(DependencyObject o, RichEditChangeState state)
    {
        (o.GetValue(RichEditChangeStateHelperProperty)
          as RichEditChangeStateHelper).State = state;
    }

    // Helper to get the state managed by the textbox
    static RichEditChangeState GetState(DependencyObject o)
    {
        return (o.GetValue(RichEditChangeStateHelperProperty)
          as RichEditChangeStateHelper).State;
    }
    #endregion
}
