using Octokit;
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class SidePanelDropDown : UserControl
    {
        public static DependencyProperty TextContentProperty = DependencyProperty.Register(
            nameof(TextContent),
            typeof(string),
            typeof(SidePanelDropDown),
            new PropertyMetadata(default(string), null)
        );

        public static DependencyProperty FlyoutProperty = DependencyProperty.Register(
            nameof(Flyout),
            typeof(FrameworkElement),
            typeof(SidePanelDropDown),
            new PropertyMetadata(default(FrameworkElement), null)
        );

        public string TextContent
        {
            get => (string)GetValue(TextContentProperty);
            set => SetValue(TextContentProperty, value);
        }

        public FrameworkElement Flyout
        {
            get => (FrameworkElement)GetValue(FlyoutProperty);
            set => SetValue(FlyoutProperty, value);
        }

        public SidePanelDropDown()
        {
            this.InitializeComponent();
        }
    }
}
