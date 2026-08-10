using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.Views.Controls.Common;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace JitHub.WinUI.Views.Controls.CodeViewer;

/// <summary>
/// JitHub-themed wrapper around WinUIEditor.CodeEditorControl (Scintilla).
/// All Scintilla API calls are deferred until the inner editor is Loaded.
/// </summary>
public sealed partial class CodeEditorControl : UserControl
{
    internal const int MaximumSynchronousEditorBytes = 128 * 1024;
    // Scintilla STYLE_DEFAULT = 32, STYLE_LINENUMBER = 33
    private const int StyleDefault = 32;
    private const int StyleLineNumber = 33;

    // Scintilla margin 0 is the line-number margin by default
    private const int LineNumberMargin = 0;

    private bool _isInnerReady;
    private bool _isThemeSubscribed;
    private bool _isSystemColorSubscribed;
    private int _allowedEditorHorizontalOffset;
    private string _appliedText = string.Empty;
    private readonly UISettings? _uiSettings;
    private readonly AccessibilitySettings? _accessibilitySettings;

    public event EventHandler? FindRequested;
    public event EventHandler<int>? CurrentLineChanged;

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
        _uiSettings = TryCreate(static () => new UISettings());
        _accessibilitySettings = TryCreate(static () => new AccessibilitySettings());
        InnerEditor.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(InnerEditor_PointerWheelChanged), true);
        InnerEditor.PreviewKeyDown += InnerEditor_PreviewKeyDown;
        InnerEditor.PointerReleased += (_, _) => PublishCurrentLine();
        InnerEditor.Loaded += OnInnerEditorLoaded;
        Unloaded += OnControlUnloaded;
    }

    public int CurrentLine => !_isInnerReady
        ? 1
        : checked((int)InnerEditor.Editor.LineFromPosition(InnerEditor.Editor.CurrentPos) + 1);

    public bool FindNext(string? query, bool reverse = false)
    {
        if (!_isInnerReady || string.IsNullOrEmpty(query)) return false;

        try
        {
            var editor = InnerEditor.Editor;
            long start = reverse ? editor.SelectionStart : editor.SelectionEnd;
            long end = reverse ? 0 : editor.Length;
            editor.SearchFlags = WinUIEditor.FindOption.None;
            editor.SetTargetRange(start, end);
            long position = editor.SearchInTarget(Encoding.UTF8.GetByteCount(query), query);
            if (position < 0)
            {
                editor.SetTargetRange(reverse ? editor.Length : 0, reverse ? 0 : editor.Length);
                position = editor.SearchInTarget(Encoding.UTF8.GetByteCount(query), query);
            }

            if (position < 0) return false;
            editor.SetSel(editor.TargetStart, editor.TargetEnd);
            editor.ScrollCaret();
            PublishCurrentLine();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] Find failed: {ex.Message}");
            return false;
        }
    }

    public void GoToLine(int oneBasedLine)
    {
        if (!_isInnerReady) return;
        try
        {
            long target = Math.Clamp((long)oneBasedLine - 1, 0, Math.Max(0, InnerEditor.Editor.LineCount - 1));
            InnerEditor.Editor.GotoLine(target);
            InnerEditor.Editor.ScrollCaret();
            PublishCurrentLine();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] Go to line failed: {ex.Message}");
        }
    }

    public bool FocusEditor() => InnerEditor.Focus(FocusState.Programmatic);

    private void InnerEditor_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool control = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        if (control && args.Key == VirtualKey.F)
        {
            args.Handled = true;
            FindRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        DispatcherQueue.TryEnqueue(PublishCurrentLine);
    }

    private void PublishCurrentLine()
    {
        if (_isInnerReady)
        {
            CurrentLineChanged?.Invoke(this, CurrentLine);
        }
    }

    private void InnerEditor_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        Microsoft.UI.Input.PointerPointProperties properties = args.GetCurrentPoint(InnerEditor).Properties;
        bool modifierHorizontal = HorizontalScrollWheelRouter.IsModifierHorizontalGesture();
        args.Handled = true;

        if (!modifierHorizontal)
        {
            QueueEditorXOffsetRestore(_allowedEditorHorizontalOffset);
            if (!properties.IsHorizontalMouseWheel)
            {
                ScrollEditorVertically(properties.MouseWheelDelta);
            }

            return;
        }

        ScrollEditorHorizontally(properties.MouseWheelDelta);
    }

    private void QueueEditorXOffsetRestore(int horizontalOffset)
    {
        RestoreEditorXOffset(horizontalOffset);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            RestoreEditorXOffset(horizontalOffset);
            _ = DispatcherQueue.TryEnqueue(() => RestoreEditorXOffset(horizontalOffset));
        });

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(40);
        int tickCount = 0;
        timer.Tick += (_, _) =>
        {
            RestoreEditorXOffset(horizontalOffset);
            tickCount++;
            if (tickCount >= 2)
            {
                timer.Stop();
            }
        };
        timer.Start();
    }

    private void RestoreEditorXOffset(int horizontalOffset)
    {
        try
        {
            if (InnerEditor.Editor.XOffset != horizontalOffset)
            {
                InnerEditor.Editor.XOffset = horizontalOffset;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] Restore horizontal offset failed: {ex.Message}");
        }
    }

    private void ScrollEditorVertically(int wheelDelta)
    {
        try
        {
            var editor = InnerEditor.Editor;
            long current = editor.FirstVisibleLine;
            long maxFirstVisibleLine = Math.Max(0, editor.LineCount - Math.Max(1, editor.LinesOnScreen));
            long lines = Math.Max(1, (long)Math.Ceiling(Math.Abs(wheelDelta) / 40d));
            long target = Math.Clamp(current + (wheelDelta > 0 ? -lines : lines), 0, maxFirstVisibleLine);
            if (target != current)
            {
                editor.FirstVisibleLine = target;
            }

            editor.XOffset = _allowedEditorHorizontalOffset;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] Vertical wheel scroll failed: {ex.Message}");
        }
    }

    private void ScrollEditorHorizontally(int wheelDelta)
    {
        try
        {
            var editor = InnerEditor.Editor;
            _allowedEditorHorizontalOffset = Math.Max(0, _allowedEditorHorizontalOffset - wheelDelta);
            editor.XOffset = _allowedEditorHorizontalOffset;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] Horizontal wheel scroll failed: {ex.Message}");
        }
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
        if (!_isThemeSubscribed)
        {
            ActualThemeChanged += OnActualThemeChanged;
            _isThemeSubscribed = true;
        }

        SubscribeSystemColors();
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        _isInnerReady = false;
        if (_isThemeSubscribed)
        {
            ActualThemeChanged -= OnActualThemeChanged;
            _isThemeSubscribed = false;
        }
        UnsubscribeSystemColors();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
        => RefreshTheme();

    private void UISettings_ColorValuesChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(RefreshTheme);

    private void AccessibilitySettings_HighContrastChanged(AccessibilitySettings sender, object args) =>
        DispatcherQueue.TryEnqueue(RefreshTheme);

    private void RefreshTheme()
    {
        ApplyLanguageId();
        ApplyFontSize();
        ApplyThemeColors();
    }

    private void SubscribeSystemColors()
    {
        if (_isSystemColorSubscribed) return;
        try
        {
            if (_uiSettings is not null)
            {
                _uiSettings.ColorValuesChanged += UISettings_ColorValuesChanged;
            }

            if (_accessibilitySettings is not null)
            {
                _accessibilitySettings.HighContrastChanged += AccessibilitySettings_HighContrastChanged;
            }

            _isSystemColorSubscribed = true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[CodeEditorControl] System color subscription failed: {exception.Message}");
        }
    }

    private void UnsubscribeSystemColors()
    {
        if (!_isSystemColorSubscribed) return;
        try
        {
            if (_uiSettings is not null)
            {
                _uiSettings.ColorValuesChanged -= UISettings_ColorValuesChanged;
            }

            if (_accessibilitySettings is not null)
            {
                _accessibilitySettings.HighContrastChanged -= AccessibilitySettings_HighContrastChanged;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[CodeEditorControl] System color unsubscription failed: {exception.Message}");
        }
        finally
        {
            _isSystemColorSubscribed = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Apply helpers
    // ──────────────────────────────────────────────────────────────────

    private void ApplyText()
    {
        try
        {
            string next = Text ?? string.Empty;
            if (string.Equals(next, _appliedText, StringComparison.Ordinal)) return;

            if (Encoding.UTF8.GetByteCount(next) > MaximumSynchronousEditorBytes)
            {
                AutomationProperties.SetItemStatus(
                    InnerEditor,
                    LocalizedResourceText.GetString(
                        "RepoCode.EditorContentTooLargeStatus",
                        "This file is available through the file actions menu."));
                return;
            }

            InnerEditor.Editor.SetText(next);
            _appliedText = next;
            AutomationProperties.SetItemStatus(
                InnerEditor,
                LocalizedResourceText.GetString("RepoCode.EditorContentReadyStatus", "Source loaded"));
            PublishCurrentLine();
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

            // Strategy:
            //   1. If language has a Lexilla config that uses a non-cpp/native lexer,
            //      load the lexer directly via CreateLexer + SetILexer for accurate
            //      tokenization (PowerShell, Bash, SQL, Python, etc.)
            //   2. If language maps to one of WinUIEdit's 8 native languages
            //      (cpp, csharp, javascript, json, html, xml, yaml, plaintext),
            //      use HighlightingLanguage for VS Code-equivalent built-in colors,
            //      then optionally override keywords for cpp-mapped languages
            //      (Java, Go, Rust, TypeScript, etc.).

            if (lexilla != null && winuiId == null)
            {
                // Lexilla-direct path
                InnerEditor.HighlightingLanguage = "plaintext";
                if (TryLoadLexilla(lexilla.LexerName))
                {
                    if (lexilla.Keywords0 != null) InnerEditor.Editor.SetKeyWords(0, lexilla.Keywords0);
                    if (lexilla.Keywords1 != null) InnerEditor.Editor.SetKeyWords(1, lexilla.Keywords1);
                    if (lexilla.Keywords2 != null) InnerEditor.Editor.SetKeyWords(2, lexilla.Keywords2);
                    if (lexilla.StyleMap != null) ApplyLexillaTokenColors(lexilla, isDark);
                }
            }
            else if (winuiId != null)
            {
                // WinUIEdit native: sets up lexer + VS Code token colors internally.
                InnerEditor.HighlightingLanguage = winuiId;

                // Apply custom keywords for cpp-mapped languages (Java, Go, Rust, TypeScript, etc.).
                var kw = ScintillaLexerDatabase.GetKeywordOverride(langId);
                if (kw != null)
                {
                    if (kw.Value.Keywords0 != null) InnerEditor.Editor.SetKeyWords(0, kw.Value.Keywords0);
                    if (kw.Value.Keywords1 != null) InnerEditor.Editor.SetKeyWords(1, kw.Value.Keywords1);
                }
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
            bool isHighContrast = IsHighContrastActive();
            Windows.UI.Color bgColor = isHighContrast
                ? GetSystemColor(UIColorType.Background, Windows.UI.Color.FromArgb(255, 0, 0, 0))
                : GetBrushColor(resources, "AppCanvasBrush");
            Windows.UI.Color fgColor = isHighContrast
                ? GetSystemColor(UIColorType.Foreground, Windows.UI.Color.FromArgb(255, 255, 255, 255))
                : TryGetBrushColor(resources, "AppOnSurfaceBrush")
                    ?? GetBrushColor(resources, "AppInkBrush");
            Windows.UI.Color mutedColor = isHighContrast
                ? fgColor
                : TryGetBrushColor(resources, "AppInkMutedBrush") ?? fgColor;
            Windows.UI.Color accentColor = isHighContrast
                ? GetSystemColor(UIColorType.Accent, Windows.UI.Color.FromArgb(255, 255, 255, 0))
                : GetResourceColor(resources, "AppAccentColor");
            Windows.UI.Color selColor = isHighContrast
                ? accentColor
                : BlendColors(bgColor, accentColor, 0.35f);

            var editor = InnerEditor.Editor;

            if (isHighContrast)
            {
                // WinUIEdit is a native Scintilla surface, so XAML theme resources do
                // not recolor its token styles. Collapse every lexer style onto system
                // foreground/background colors and provide an explicit selected-text
                // contrast pair instead of assuming the light/dark token palette works.
                for (int style = 0; style <= 127; style++)
                {
                    editor.StyleSetBack(style, ToBgr(bgColor));
                    editor.StyleSetFore(style, ToBgr(fgColor));
                }

                editor.SetSelFore(true, ToBgr(bgColor));
                AutomationProperties.SetHelpText(
                    InnerEditor,
                    LocalizedResourceText.GetString(
                        "RepoCode/EditorHighContrastStatus",
                        "High contrast editor colors active"));
            }
            else
            {
                editor.SetSelFore(false, 0);
                AutomationProperties.SetHelpText(InnerEditor, string.Empty);
            }

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

            EditorSurface.Background = new SolidColorBrush(bgColor);
            EditorSurface.BorderBrush = new SolidColorBrush(isHighContrast ? fgColor : GetBrushColor(resources, "AppOutlineBrush"));
            EditorSurface.BorderThickness = isHighContrast ? new Thickness(2) : new Thickness(1);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] ApplyThemeColors failed: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Lexilla integration via P/Invoke into WinUIEditor.dll
    //
    // WinUIEditor.dll exports the Lexilla C API (CreateLexer, GetLexerCount,
    // GetLexerName, etc.) since Lexilla is statically linked into it.
    // We P/Invoke CreateLexer to obtain an ILexer5* pointer and pass it to
    // Editor.SetILexer (Scintilla message 4033). This is the same path
    // WinUIEdit's C++ code uses internally for its 8 built-in languages,
    // unlocking the full ~120 Lexilla lexers for our app.
    //
    // The deprecated SCI_SETLEXERLANGUAGE (4006) is NOT supported by modern
    // Scintilla and was a no-op — that bug is what caused PowerShell, Bash,
    // Batch, etc. to render as plain text in earlier iterations.
    // ──────────────────────────────────────────────────────────────────

    [DllImport("WinUIEditor.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr CreateLexer([MarshalAs(UnmanagedType.LPStr)] string name);

    private bool TryLoadLexilla(string lexerName)
    {
        try
        {
            IntPtr ptr = CreateLexer(lexerName);
            if (ptr == IntPtr.Zero)
            {
                Debug.WriteLine($"[CodeEditorControl] Lexilla CreateLexer('{lexerName}') returned null.");
                return false;
            }

            InnerEditor.Editor.ClearDocumentStyle();
            // Editor.SetILexer corresponds to SCI_SETILEXER (4033) — wParam ignored,
            // lParam is an ILexer5* cast to UInt64.
            InnerEditor.Editor.SetILexer((ulong)ptr.ToInt64());
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeEditorControl] TryLoadLexilla('{lexerName}') failed: {ex.Message}");
            return false;
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

    private bool IsHighContrastActive()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_HIGH_CONTRAST"),
                "1",
                StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return _accessibilitySettings?.HighContrast == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private Windows.UI.Color GetSystemColor(UIColorType colorType, Windows.UI.Color fallback)
    {
        try
        {
            return _uiSettings?.GetColorValue(colorType) ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static T? TryCreate<T>(Func<T> factory)
        where T : class
    {
        try
        {
            return factory();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
