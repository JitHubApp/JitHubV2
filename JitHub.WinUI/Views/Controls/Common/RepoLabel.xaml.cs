using System;
using JitHub.Models;
using JitHub.Models.Base;
using Octokit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Common
{
    public sealed partial class RepoLabel : UserControl
    {
        private static readonly Brush TransparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        private static readonly Brush BlackBrush = new SolidColorBrush(Microsoft.UI.Colors.Black);
        private static readonly Brush WhiteBrush = new SolidColorBrush(Microsoft.UI.Colors.White);

        public static DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label),
            typeof(object),
            typeof(RepoLabel),
            new PropertyMetadata(default(object), null));
        
        public object? Label
        {
            get => GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public RepoLabel()
        {
            this.InitializeComponent();
        }

        public string GetName(object? label)
            => ResolveLabel(label)?.Name ?? string.Empty;

        public string GetDescription(object? label)
            => ResolveLabel(label)?.Description ?? string.Empty;

        public Brush GetBackgroundBrush(object? label)
            => TryParseColor(ResolveLabel(label)?.Color, out var color)
                ? new SolidColorBrush(color)
                : TransparentBrush;

        public Brush GetForegroundBrush(object? label)
        {
            if (!TryParseColor(ResolveLabel(label)?.Color, out var color))
            {
                return BlackBrush;
            }

            var perceivedBrightness = Math.Sqrt(
                color.R * color.R * .299 +
                color.G * color.G * .587 +
                color.B * color.B * .114);

            return perceivedBrightness > 130 ? BlackBrush : WhiteBrush;
        }

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

