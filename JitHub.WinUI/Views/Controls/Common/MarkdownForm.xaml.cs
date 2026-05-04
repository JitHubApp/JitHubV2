using CommunityToolkit.WinUI.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Services;
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Common
{
    public sealed partial class MarkdownForm : UserControl
    {

        private readonly IGitHubService _gitHubService;

        public static DependencyProperty ActionContentProperty = DependencyProperty.Register(
            nameof(ActionContent),
            typeof(object),
            typeof(MarkdownForm),
            new PropertyMetadata(null, OnActionContentChanged));

        public static DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(MarkdownForm),
            new PropertyMetadata(default(string), null));
        public static DependencyProperty FormPaddingProperty = DependencyProperty.Register(
            nameof(FormPadding),
            typeof(Thickness),
            typeof(MarkdownForm),
            new PropertyMetadata(new Thickness(0), null));
        public static DependencyProperty EditorHeightProperty = DependencyProperty.Register(
            nameof(EditorHeight),
            typeof(double),
            typeof(MarkdownForm),
            new PropertyMetadata(220d, null));

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

        public MarkdownConfig Config => _gitHubService.GetMarkdownConfig();

        public MarkdownForm()
        {
            this.InitializeComponent();
            _gitHubService = Ioc.Default.GetService<IGitHubService>()
                ?? throw new InvalidOperationException("IGitHubService is not registered.");
        }

        private static void OnActionContentChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        {
            if (dependencyObject is MarkdownForm form)
            {
                form.ActionContentPresenter.Content = args.NewValue;
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



