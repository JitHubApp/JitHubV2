using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.CodeViewer;

/// <summary>
/// JitHub-themed wrapper around WinUIEditor.CodeEditorControl (Scintilla).
/// All Scintilla API calls are deferred until the inner editor is Loaded.
/// </summary>
public sealed partial class CodeEditorControl : UserControl
{
    // Scintilla STYLE_DEFAULT = 32, STYLE_LINENUMBER = 33
    private const int StyleDefault = 32;
    private const int StyleLineNumber = 33;

    // Scintilla margin 0 is the line-number margin by default
    private const int LineNumberMargin = 0;

    private bool _isInnerReady;

    // ──────────────────────────────────────────────────────────────────
    // DependencyProperty: Text
    // ──────────────────────────────────────────────────────────────────
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(CodeEditorControl),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (CodeEditorControl)d;
        if (!ctrl._isInnerReady) return;
        ctrl.ApplyText();
    }

    // ──────────────────────────────────────────────────────────────────
    // DependencyProperty: LanguageId
    // ──────────────────────────────────────────────────────────────────
    public static readonly DependencyProperty LanguageIdProperty =
        DependencyProperty.Register(
            nameof(LanguageId),
            typeof(string),
            typeof(CodeEditorControl),
            new PropertyMetadata("plaintext", OnLanguageIdChanged));

    public string LanguageId
    {
        get => (string)GetValue(LanguageIdProperty);
        set => SetValue(LanguageIdProperty, value);
    }

    private static void OnLanguageIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (CodeEditorControl)d;
        if (!ctrl._isInnerReady) return;
        ctrl.ApplyLanguageId();
        ctrl.ApplyFontSize();   // re-apply size since HighlightingLanguage resets styles
        ctrl.ApplyThemeColors();
    }

    // ──────────────────────────────────────────────────────────────────
    // DependencyProperty: IsReadOnlyEditor
    // ──────────────────────────────────────────────────────────────────
    public static readonly DependencyProperty IsReadOnlyEditorProperty =
        DependencyProperty.Register(
            nameof(IsReadOnlyEditor),
            typeof(bool),
            typeof(CodeEditorControl),
            new PropertyMetadata(true, OnIsReadOnlyEditorChanged));

    /// <summary>
    /// Alias for read-only mode; named IsReadOnlyEditor to avoid clash with FrameworkElement.IsReadOnly.
    /// </summary>
    public bool IsReadOnlyEditor
    {
        get => (bool)GetValue(IsReadOnlyEditorProperty);
        set => SetValue(IsReadOnlyEditorProperty, value);
    }

    private static void OnIsReadOnlyEditorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (CodeEditorControl)d;
        if (!ctrl._isInnerReady) return;
        ctrl.ApplyReadOnly();
    }

    // ──────────────────────────────────────────────────────────────────
    // DependencyProperty: FontSize (shadows FrameworkElement.FontSize intentionally)
    // ──────────────────────────────────────────────────────────────────
    public static readonly new DependencyProperty FontSizeProperty =
        DependencyProperty.Register(
            nameof(FontSize),
            typeof(double),
            typeof(CodeEditorControl),
            new PropertyMetadata(13.0, OnFontSizeChanged));

    public new double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    private static void OnFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (CodeEditorControl)d;
        if (!ctrl._isInnerReady) return;
        ctrl.ApplyFontSize();
    }

    // ──────────────────────────────────────────────────────────────────
    // DependencyProperty: ShowLineNumbers
    // ──────────────────────────────────────────────────────────────────
    public static readonly DependencyProperty ShowLineNumbersProperty =
        DependencyProperty.Register(
            nameof(ShowLineNumbers),
            typeof(bool),
            typeof(CodeEditorControl),
            new PropertyMetadata(true, OnShowLineNumbersChanged));

    public bool ShowLineNumbers
    {
        get => (bool)GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    private static void OnShowLineNumbersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (CodeEditorControl)d;
        if (!ctrl._isInnerReady) return;
        ctrl.ApplyShowLineNumbers();
    }

    // ──────────────────────────────────────────────────────────────────
    // DependencyProperty: WordWrap
    // ──────────────────────────────────────────────────────────────────
    public static readonly DependencyProperty WordWrapProperty =
        DependencyProperty.Register(
            nameof(WordWrap),
            typeof(bool),
            typeof(CodeEditorControl),
            new PropertyMetadata(false, OnWordWrapChanged));

    public bool WordWrap
    {
        get => (bool)GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    private static void OnWordWrapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (CodeEditorControl)d;
        if (!ctrl._isInnerReady) return;
        ctrl.ApplyWordWrap();
    }

    // ──────────────────────────────────────────────────────────────────
    // DependencyProperty: FilePath (informational)
    // ──────────────────────────────────────────────────────────────────
    public static readonly DependencyProperty FilePathProperty =
        DependencyProperty.Register(
            nameof(FilePath),
            typeof(string),
            typeof(CodeEditorControl),
            new PropertyMetadata(string.Empty));

    public string FilePath
    {
        get => (string)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    // ──────────────────────────────────────────────────────────────────
    // Construction and Loaded
    // ──────────────────────────────────────────────────────────────────

    public CodeEditorControl()
    {
        InitializeComponent();
        InnerEditor.Loaded += OnInnerEditorLoaded;
    }

    private void OnInnerEditorLoaded(object sender, RoutedEventArgs e)
    {
        _isInnerReady = true;
        ApplyLanguageId();      // sets up lexer + token colors (WinUIEdit may call StyleClearAll)
        ApplyFontSize();        // re-apply font sizes for all styles (after language reset)
        ApplyThemeColors();     // override bg/fg/linenumber with app theme
        ApplyText();
        ApplyReadOnly();
        ApplyShowLineNumbers();
        ApplyWordWrap();
        ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyLanguageId();
        ApplyFontSize();
        ApplyThemeColors();
    }

    // ──────────────────────────────────────────────────────────────────
    // Apply helpers
    // ──────────────────────────────────────────────────────────────────

    private void ApplyText()
    {
        try
        {
            InnerEditor.Editor.SetText(Text ?? string.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] ApplyText failed: {ex.Message}");
        }
    }

    private void ApplyLanguageId()
    {
        string langId = LanguageId ?? "plaintext";
        try
        {
            string? winuiId = ScintillaLexerDatabase.GetWinUIEditId(langId);
            ScintillaLexerDatabase.LexillaConfig? lexilla = ScintillaLexerDatabase.GetLexillaConfig(langId);
            bool isDark = ActualTheme == ElementTheme.Dark;

            if (winuiId != null)
            {
                // WinUIEdit native: sets up lexer + VS Code token colors internally.
                InnerEditor.HighlightingLanguage = winuiId;

                // Apply custom keywords for languages mapped to "cpp" (Java, Go, Rust, etc.)
                var kw = ScintillaLexerDatabase.GetKeywordOverride(langId);
                if (kw != null)
                {
                    if (kw.Value.Keywords0 != null) SendKeywords(0, kw.Value.Keywords0);
                    if (kw.Value.Keywords1 != null) SendKeywords(1, kw.Value.Keywords1);
                }

                // Apply extra token colors for cpp-mapped Lexilla configs where relevant
                if (lexilla?.StyleMap != null)
                    ApplyLexillaTokenColors(lexilla, isDark);
            }
            else if (lexilla != null)
            {
                // Lexilla-direct: reset to plaintext first (clears any prior lexer),
                // then switch to the Lexilla lexer and apply our own token colors.
                InnerEditor.HighlightingLanguage = "plaintext";
                SendLexerLanguage(lexilla.LexerName);
                if (lexilla.Keywords0 != null) SendKeywords(0, lexilla.Keywords0);
                if (lexilla.Keywords1 != null) SendKeywords(1, lexilla.Keywords1);
                if (lexilla.StyleMap != null) ApplyLexillaTokenColors(lexilla, isDark);
            }
            else
            {
                InnerEditor.HighlightingLanguage = "plaintext";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] ApplyLanguageId({langId}) failed: {ex.Message}");
        }
    }

    private void ApplyReadOnly()
    {
        try
        {
            InnerEditor.Editor.ReadOnly = IsReadOnlyEditor;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] ApplyReadOnly failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the font size on every Scintilla style (0–127) without calling StyleClearAll,
    /// so syntax-highlighting token colors are preserved.
    /// </summary>
    private void ApplyFontSize()
    {
        try
        {
            int fractional = (int)(FontSize * 100);
            var editor = InnerEditor.Editor;
            for (int i = 0; i <= 127; i++)
                editor.StyleSetSizeFractional(i, fractional);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] ApplyFontSize failed: {ex.Message}");
        }
    }

    private void ApplyShowLineNumbers()
    {
        try
        {
            // Margin 0 is the default line-number margin in Scintilla
            // Width 0 hides it; ~40px shows 4-digit line numbers
            int width = ShowLineNumbers ? 40 : 0;
            InnerEditor.Editor.SetMarginWidthN(LineNumberMargin, width);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] ApplyShowLineNumbers failed: {ex.Message}");
        }
    }

    private void ApplyWordWrap()
    {
        try
        {
            // SC_WRAP_NONE = 0, SC_WRAP_WORD = 1
            InnerEditor.Editor.WrapMode = WordWrap
                ? WinUIEditor.Wrap.Word
                : WinUIEditor.Wrap.None;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] ApplyWordWrap failed: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Theme color application (never resets lexer/token colors)
    // ──────────────────────────────────────────────────────────────────

    private void ApplyThemeColors()
    {
        try
        {
            var resources = Application.Current.Resources;

            var bgColor = GetBrushColor(resources, "AppCanvasBrush");
            var fgColor = TryGetBrushColor(resources, "AppOnSurfaceBrush")
                          ?? GetBrushColor(resources, "AppInkBrush");
            var mutedColor = TryGetBrushColor(resources, "AppInkMutedBrush") ?? fgColor;
            var accentColor = GetResourceColor(resources, "AppAccentColor");
            var selColor = BlendColors(bgColor, accentColor, 0.35f);

            var editor = InnerEditor.Editor;

            // Override STYLE_DEFAULT bg/fg. We intentionally skip StyleClearAll() to
            // preserve the lexer token colors set by ApplyLanguageId().
            editor.StyleSetBack(StyleDefault, ToBgr(bgColor));
            editor.StyleSetFore(StyleDefault, ToBgr(fgColor));

            // Override STYLE_LINENUMBER (33) so gutter never shows white in dark theme.
            editor.StyleSetBack(StyleLineNumber, ToBgr(bgColor));
            editor.StyleSetFore(StyleLineNumber, ToBgr(mutedColor));

            editor.SetSelBack(true, ToBgr(selColor));
            editor.CaretFore = ToBgr(fgColor);
            try { editor.SetWhitespaceBack(true, ToBgr(bgColor)); } catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] ApplyThemeColors failed: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Lexilla via SendMessage
    // ──────────────────────────────────────────────────────────────────

    private void SendLexerLanguage(string lexerName)
    {
        IntPtr ptr = Marshal.StringToHGlobalAnsi(lexerName);
        try
        {
            InnerEditor.SendMessage(
                (WinUIEditor.ScintillaMessage)ScintillaLexerDatabase.SciSetLexerLanguage,
                0,
                ptr.ToInt64());
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void SendKeywords(int set, string keywords)
    {
        IntPtr ptr = Marshal.StringToHGlobalAnsi(keywords);
        try
        {
            InnerEditor.SendMessage(
                (WinUIEditor.ScintillaMessage)ScintillaLexerDatabase.SciSetKeyWords,
                (ulong)set,
                ptr.ToInt64());
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void ApplyLexillaTokenColors(ScintillaLexerDatabase.LexillaConfig config, bool isDark)
    {
        if (config.StyleMap is null) return;
        var editor = InnerEditor.Editor;
        foreach (var (styleId, kind) in config.StyleMap)
        {
            var color = ScintillaLexerDatabase.TokenColor(kind, isDark);
            editor.StyleSetFore(styleId, ToBgr(color));
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Color helpers
    // ──────────────────────────────────────────────────────────────────

    /// <summary>Convert Windows.UI.Color to Scintilla BGR integer (COLORREF format).</summary>
    private static int ToBgr(Windows.UI.Color c)
        => c.R | (c.G << 8) | (c.B << 16);

    private static Windows.UI.Color GetBrushColor(ResourceDictionary resources, string key)
    {
        if (resources[key] is SolidColorBrush brush)
            return brush.Color;
        return Windows.UI.Color.FromArgb(255, 0, 0, 0);
    }

    private static Windows.UI.Color? TryGetBrushColor(ResourceDictionary resources, string key)
    {
        if (resources.ContainsKey(key) && resources[key] is SolidColorBrush brush)
            return brush.Color;
        return null;
    }

    private static Windows.UI.Color GetResourceColor(ResourceDictionary resources, string key)
    {
        if (resources.ContainsKey(key) && resources[key] is Windows.UI.Color color)
            return color;
        return Windows.UI.Color.FromArgb(255, 0x3E, 0x7B, 0x64);
    }

    /// <summary>
    /// Blend <paramref name="overlay"/> over <paramref name="base"/> using straight alpha.
    /// <paramref name="overlayAlpha"/> is in [0, 1].
    /// </summary>
    private static Windows.UI.Color BlendColors(
        Windows.UI.Color @base,
        Windows.UI.Color overlay,
        float overlayAlpha)
    {
        float a = overlayAlpha;
        byte r = (byte)(@base.R + (overlay.R - @base.R) * a);
        byte g = (byte)(@base.G + (overlay.G - @base.G) * a);
        byte b = (byte)(@base.B + (overlay.B - @base.B) * a);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }
}
