using CommunityToolkit.WinUI.Controls;
using JitHub.Markdown;
using JitHub.Markdown.Runtime;
using JitHub.WinUI.ViewModels;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using MarkdownRenderer.Images;
using Windows.UI.ViewManagement;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Common
{
    [WinRT.GeneratedBindableCustomProperty]
    public sealed partial class MarkdownForm : UserControl
    {
        private readonly MarkdownFormAutomationIdentity _automationIdentity = new();
        private readonly RuntimeEventSubscription _runtimeEvents;
        private readonly UISettings? _uiSettings;
        private bool _textScaleSubscribed;

        public MarkdownFormViewModel ViewModel { get; } = new();

        public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
            nameof(ActionContent),
            typeof(object),
            typeof(MarkdownForm),
            new PropertyMetadata(null, OnActionContentChanged));

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(MarkdownForm),
            new PropertyMetadata(default(string), OnBindablePropertyChanged));
        public static readonly DependencyProperty FormPaddingProperty = DependencyProperty.Register(
            nameof(FormPadding),
            typeof(Thickness),
            typeof(MarkdownForm),
            new PropertyMetadata(new Thickness(0), OnBindablePropertyChanged));
        public static readonly DependencyProperty EditorHeightProperty = DependencyProperty.Register(
            nameof(EditorHeight),
            typeof(double),
            typeof(MarkdownForm),
            new PropertyMetadata(220d, OnBindablePropertyChanged));
        public static readonly DependencyProperty DocumentSourceProperty = DependencyProperty.Register(
            nameof(DocumentSource),
            typeof(MarkdownDocumentSource),
            typeof(MarkdownForm),
            new PropertyMetadata(null, OnDocumentSourceChanged));
        public static readonly DependencyProperty AutomationInstanceIdProperty = DependencyProperty.Register(
            nameof(AutomationInstanceId),
            typeof(string),
            typeof(MarkdownForm),
            new PropertyMetadata(null, OnAutomationInstanceIdChanged));

        public string? Text
        {
            get => (string?)GetValue(TextProperty);
            set
            {
                SetValue(TextProperty, value);
            }
        }

        public object? ActionContent
        {
            get => GetValue(ActionContentProperty);
            set
            {
                SetValue(ActionContentProperty, value);
            }
        }

        public Thickness FormPadding
        {
            get => (Thickness)GetValue(FormPaddingProperty);
            set => SetValue(FormPaddingProperty, value);
        }

        public double EditorHeight
        {
            get => (double)GetValue(EditorHeightProperty);
            set => SetValue(EditorHeightProperty, value);
        }

        public bool FocusEditor(FocusState focusState = FocusState.Programmatic) =>
            EditorTextBox.Focus(focusState);

        public double EffectiveEditorHeight
        {
            get
            {
                double scale = GetSystemTextScaleFactor();
                double scaledHeight = EditorHeight * System.Math.Clamp(scale, 1, 3);
                return System.Math.Min(scaledHeight, EditorHeight + 180);
            }
        }

        public MarkdownForm()
        {
            global::JitHub.WinUI.App app = (global::JitHub.WinUI.App)Application.Current;
            _runtimeEvents = app.MarkdownRuntimeEvents;
            _uiSettings = _runtimeEvents.TryCreate(static () => new UISettings(), nameof(UISettings));
            this.InitializeComponent();
            Loaded += MarkdownForm_Loaded;
            Unloaded += MarkdownForm_Unloaded;
        }

        public string? AutomationInstanceId
        {
            get => (string?)GetValue(AutomationInstanceIdProperty);
            set => SetValue(AutomationInstanceIdProperty, value);
        }

        public string EffectiveAutomationInstanceId => ResolveAutomationPrefix();

        public string EffectivePreviewAutomationInstanceId =>
            MarkdownFormAutomationIdentity.GetPreviewInstanceId(ResolveAutomationPrefix());

        public MarkdownDocumentSource? DocumentSource
        {
            get => (MarkdownDocumentSource?)GetValue(DocumentSourceProperty);
            set => SetValue(DocumentSourceProperty, value);
        }

        private void MarkdownForm_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeTextScale();
            Bindings.Update();
            ApplyAutomationIdentity();
            // Automation identity is not document identity. An anonymous editor must
            // remain anonymous so recycled controls cannot retain remote-content consent.
            PreviewViewer.DocumentSource = DocumentSource;
        }

        private void MarkdownForm_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_uiSettings is null || !_textScaleSubscribed)
            {
                return;
            }

            _runtimeEvents.TryUnsubscribe(
                () => _uiSettings.TextScaleFactorChanged -= UISettings_TextScaleFactorChanged,
                _textScaleSubscribed,
                nameof(UISettings.TextScaleFactorChanged));
            _textScaleSubscribed = false;
        }

        private void SubscribeTextScale()
        {
            if (_uiSettings is null || _textScaleSubscribed)
            {
                return;
            }

            _textScaleSubscribed = _runtimeEvents.TrySubscribe(
                () => _uiSettings.TextScaleFactorChanged += UISettings_TextScaleFactorChanged,
                nameof(UISettings.TextScaleFactorChanged));
        }

        private void UISettings_TextScaleFactorChanged(UISettings sender, object args)
            => DispatcherQueue.TryEnqueue(Bindings.Update);

        private double GetSystemTextScaleFactor()
        {
            try
            {
                return System.Math.Clamp(_uiSettings?.TextScaleFactor ?? 1, 1, 3);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return 1;
            }
        }

        private void ApplyAutomationIdentity()
        {
            MarkdownFormPeerAutomationIds ids = MarkdownFormAutomationIdentity.GetPeerIds(ResolveAutomationPrefix());
            AutomationProperties.SetAutomationId(ModeSegmented, ids.Mode);
            AutomationProperties.SetAutomationId(WriteModeItem, ids.WriteMode);
            AutomationProperties.SetAutomationId(PreviewModeItem, ids.PreviewMode);
            AutomationProperties.SetAutomationId(EditorTextBox, ids.Editor);
            AutomationProperties.SetAutomationId(PreviewViewer, ids.Preview);
            PreviewViewer.AutomationInstanceId = ids.Preview;
        }

        private string ResolveAutomationPrefix() =>
            _automationIdentity.ResolvePrefix(AutomationInstanceId, AutomationProperties.GetAutomationId(this));

        private static void OnAutomationInstanceIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not MarkdownForm form)
            {
                return;
            }

            form.Bindings.Update();
            form.ApplyAutomationIdentity();
        }

        private static void OnDocumentSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MarkdownForm form)
            {
                form.PreviewViewer.DocumentSource = e.NewValue as MarkdownDocumentSource;
            }
        }

        private static void OnActionContentChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        {
            if (dependencyObject is MarkdownForm form)
            {
                form.ActionContentPresenter.Content = args.NewValue;
            }
        }

        private static void OnBindablePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MarkdownForm self)
            {
                self.Bindings.Update();
            }
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.Text = sender is TextBox textBox
                ? textBox.Text
                : Text ?? string.Empty;
        }

        private void BodyModeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not Segmented segmented)
            {
                return;
            }

            ViewModel.SelectedBodyView = segmented.SelectedIndex == 1 ? "Preview" : "Write";
        }
    }
}



