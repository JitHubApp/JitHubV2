using Microsoft.Toolkit.Uwp.UI.Helpers;
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class SvgIcon : UserControl
    {
        private ThemeListener _themeListener = new ThemeListener();
        public static DependencyProperty DarkSourceProperty = DependencyProperty.Register(
            nameof(DarkSource),
            typeof(string),
            typeof(SvgIcon),
            new PropertyMetadata(default(string), OnDarkSourceChange)
        );

        public static DependencyProperty LightSourceProperty = DependencyProperty.Register(
            nameof(LightSource),
            typeof(string),
            typeof(SvgIcon),
            new PropertyMetadata(default(string), OnLightSourceChange)
        );

        public static DependencyProperty IconHeightProperty = DependencyProperty.Register(
            nameof(IconHeight),
            typeof(double),
            typeof(SvgIcon),
            new PropertyMetadata(default(double), null)
        );

        public static DependencyProperty IconWidthProperty = DependencyProperty.Register(
            nameof(IconWidth),
            typeof(double),
            typeof(SvgIcon),
            new PropertyMetadata(default(double), null)
        );

        public static void OnDarkSourceChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SvgIcon self && e.NewValue != null)
            {
                var source = (string)e.NewValue;
                if (self.ThemeListener.CurrentTheme == ApplicationTheme.Dark)
                {
                    self.IconImage.Source = new SvgImageSource(new Uri(source));
                }
            }
        }

        public static void OnLightSourceChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SvgIcon self && e.NewValue != null)
            {
                var source = (string)e.NewValue;
                if (self.ThemeListener.CurrentTheme == ApplicationTheme.Light)
                {
                    self.IconImage.Source = new SvgImageSource(new Uri(source));
                }
            }
        }

        public string DarkSource
        {
            get => (string)GetValue(DarkSourceProperty);
            set => SetValue(DarkSourceProperty, value);
        }
        public string LightSource
        {
            get => (string)GetValue(LightSourceProperty);
            set => SetValue(LightSourceProperty, value);
        }
        public double IconHeight
        {
            get => (double)GetValue(HeightProperty);
            set => SetValue(HeightProperty, value);
        }
        public double IconWidth
        {
            get => (double)GetValue(IconWidthProperty);
            set => SetValue(IconWidthProperty, value);
        }
        public ThemeListener ThemeListener { get => _themeListener; }
        public SvgIcon()
        {
            this.InitializeComponent();
            _themeListener.ThemeChanged += ListenerThemeChanged;
        }
        private void ListenerThemeChanged(ThemeListener sender)
        {
            IconImage.Source = _themeListener.CurrentTheme == ApplicationTheme.Dark ? new SvgImageSource(new Uri(DarkSource)) : new SvgImageSource(new Uri(LightSource));
        }
    }
}
