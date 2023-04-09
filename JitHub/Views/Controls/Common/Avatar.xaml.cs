using JitHub.Models;
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class Avatar : UserControl
    {
        public static DependencyProperty SizeProperty = DependencyProperty.Register(
            "Size",
            typeof(UISize),
            typeof(Avatar),
            new PropertyMetadata(default(UISize), null));
        public static DependencyProperty UrlProperty = DependencyProperty.Register(
            "Url",
            typeof(string),
            typeof(Avatar),
            new PropertyMetadata(default(string), OnUrlChanged));
        public static DependencyProperty LoginProperty = DependencyProperty.Register(
            "Login",
            typeof(string),
            typeof(Avatar),
            new PropertyMetadata(default(string), null));
        public static DependencyProperty ShowLoginProperty = DependencyProperty.Register(
            "ShowLogin",
            typeof(bool),
            typeof(Avatar),
            new PropertyMetadata(default(string), null));

        private static void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Avatar self)
            {
                if (e.NewValue != null && !string.IsNullOrWhiteSpace((string)e.NewValue))
                {

                    self.ProfilePic.Source = new BitmapImage(new Uri((string)e.NewValue));
                }
                else
                {
                    self.ProfilePic.Source = new BitmapImage(new Uri("ms-appx:///Assets/Octocat.png"));
                }
            }
        }


        public UISize Size
        {
            get => (UISize)GetValue(SizeProperty);
            set
            {
                SetValue(SizeProperty, value);
            }
        }
        public string Url
        {
            get => (string)GetValue(UrlProperty);
            set
            {
                SetValue(UrlProperty, value);
            }
        }

        public string Login
        {
            get => (string)GetValue(LoginProperty);
            set
            {
                SetValue(LoginProperty, value);
            }
        }

        public bool ShowLogin
        {
            get => (bool)GetValue(ShowLoginProperty);
            set
            {
                SetValue(ShowLoginProperty, value);
            }
        }

        public Avatar()
        {
            this.InitializeComponent();
        }
    }
}
