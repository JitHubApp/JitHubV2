using System;
using JitHub.Models;
using JitHub.Models.Base;
using JitHub.Models.LegacyGitHub;
using JitHub.Services.Accessibility;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Common
{
    public sealed partial class RepoLabel : UserControl
    {
        private AppThemeSettingsMonitor? _themeSettings;
        private bool _isHighContrastSubscribed;

        public static DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label),
            typeof(object),
            typeof(RepoLabel),
            new PropertyMetadata(default(object), OnLabelChanged));

        public object? Label
        {
            get => GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public RepoLabel()
        {
            this.InitializeComponent();
            Loaded += RepoLabel_Loaded;
            Unloaded += RepoLabel_Unloaded;
        }

        private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RepoLabel self)
            {
                self.Bindings.Update();
            }
        }

        public string GetName(object? label)
            => ResolveLabel(label)?.Name ?? string.Empty;

        public string GetDescription(object? label)
            => ResolveLabel(label)?.Description ?? string.Empty;

        public Brush GetBackgroundBrush(object? label)
        {
            if (IsHighContrastActive())
            {
                return GetThemeBrush(HighContrastVisualPolicy.AccentBrushKey);
            }

            return TryParseColor(ResolveLabel(label)?.Color, out var color)
                ? new SolidColorBrush(color)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        public Brush GetForegroundBrush(object? label)
        {
            bool hasColor = TryParseColor(ResolveLabel(label)?.Color, out var color);
            double perceivedBrightness = hasColor
                ? Math.Sqrt(
                    color.R * color.R * .299 +
                    color.G * color.G * .587 +
                    color.B * color.B * .114)
                : double.MaxValue;
            RepositoryLabelBrushPolicy policy = HighContrastVisualPolicy.GetRepositoryLabelPolicy(
                IsHighContrastActive(),
                hasColor,
                useDarkText: perceivedBrightness > 130);
            return GetThemeBrush(policy.ForegroundResourceKey);
        }

        private void RepoLabel_Loaded(object sender, RoutedEventArgs e)
        {
            _themeSettings ??= ThemeSettingsHelper.TryGetFor(this);
            if (_themeSettings is not null && !_isHighContrastSubscribed)
            {
                try
                {
                    _themeSettings.Changed += ThemeSettings_Changed;
                    _isHighContrastSubscribed = true;
                }
                catch (Exception)
                {
                    _isHighContrastSubscribed = false;
                }
            }

            Bindings.Update();
        }

        private void RepoLabel_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_themeSettings is null || !_isHighContrastSubscribed)
            {
                _themeSettings = null;
                return;
            }

            try
            {
                _themeSettings.Changed -= ThemeSettings_Changed;
            }
            catch (Exception)
            {
                // The system projection can already be unavailable during app shutdown.
            }
            finally
            {
                _isHighContrastSubscribed = false;
                _themeSettings = null;
            }
        }

        private void ThemeSettings_Changed(object? sender, EventArgs args) =>
            DispatcherQueue.TryEnqueue(Bindings.Update);

        private bool IsHighContrastActive() =>
            ThemeSettingsHelper.IsHighContrastActive(_themeSettings);

        private static Brush GetThemeBrush(string resourceKey) =>
            Application.Current.Resources.TryGetValue(resourceKey, out object? resource) && resource is Brush brush
                ? brush
                : throw new InvalidOperationException($"Required theme brush '{resourceKey}' is unavailable.");

        private static Label? ResolveLabel(object? value)
            => value switch
            {
                Label label => label,
                SelectableLabel selectableLabel => selectableLabel.Label,
                RepoSelectableItemModel<Label> labelModel => labelModel.Model,
                _ => null
            };

        private static bool TryParseColor(string? hexColor, out Color color)
        {
            color = default;

            if (string.IsNullOrWhiteSpace(hexColor))
            {
                return false;
            }

            var normalized = hexColor.Trim().TrimStart('#');
            if (normalized.Length == 6)
            {
                normalized += "FF";
            }

            if (normalized.Length != 8)
            {
                return false;
            }

            try
            {
                var r = (byte)Convert.ToUInt32(normalized.Substring(0, 2), 16);
                var g = (byte)Convert.ToUInt32(normalized.Substring(2, 2), 16);
                var b = (byte)Convert.ToUInt32(normalized.Substring(4, 2), 16);
                var a = (byte)Convert.ToUInt32(normalized.Substring(6, 2), 16);
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }
}

