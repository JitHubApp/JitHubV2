using System;
using System.Numerics;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Profile
{
    public sealed partial class ProfileButton : UserControl
    {
        public static DependencyProperty OnClickCommandProperty = DependencyProperty.Register(
            nameof(OnClickCommand),
            typeof(ICommand),
            typeof(ProfileButton),
            new PropertyMetadata(null));

        public ICommand OnClickCommand
        {
            get => (ICommand)GetValue(OnClickCommandProperty);
            set
            {
                SetValue(OnClickCommandProperty, value);
                ViewModel.GoToProfilePageCommand = value;
            }
        }
        
        public ProfileButton()
        {
            this.InitializeComponent();
        }

        private void Button_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ((Control)sender).RenderTransform = new ScaleTransform();
            var visual = ((Control)sender).RenderTransform as ScaleTransform;
            visual.CenterX = ((Control)sender).Width / 2;
            visual.CenterY = ((Control)sender).Height / 2;
            var storyboard = new Storyboard();
            // Create a double animation to animate the ScaleX property from 1 to 0.9
            var scaleXAnimation = new DoubleAnimation()
            {
                From = 1,
                To = 0.9,
                Duration = TimeSpan.FromMilliseconds(200)
            };
            // Set the target of the animation to be the scale transform of the control
            Storyboard.SetTarget(scaleXAnimation, ((Control)sender).RenderTransform);
            // Set the target property of the animation to be the ScaleX property
            Storyboard.SetTargetProperty(scaleXAnimation, "ScaleX");
            // Add the animation to the storyboard
            storyboard.Children.Add(scaleXAnimation);
            // Create a double animation to animate the ScaleY property from 1 to 0.9
            var scaleYAnimation = new DoubleAnimation()
            {
                From = 1,
                To = 0.9,
                Duration = TimeSpan.FromMilliseconds(200)
            };
            // Set the target of the animation to be the scale transform of the control
            Storyboard.SetTarget(scaleYAnimation, ((Control)sender).RenderTransform);
            // Set the target property of the animation to be the ScaleY property
            Storyboard.SetTargetProperty(scaleYAnimation, "ScaleY");
            // Add the animation to the storyboard
            storyboard.Children.Add(scaleYAnimation);
            // Start the storyboard
            storyboard.Begin();
        }

        private void Button_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ((Control)sender).RenderTransform = new ScaleTransform();
            var visual = ((Control)sender).RenderTransform as ScaleTransform;
            visual.CenterX = ((Control)sender).Width / 2;
            visual.CenterY = ((Control)sender).Height / 2;
            var storyboard = new Storyboard();
            // Create a double animation to animate the ScaleX property from 0.9 to 1
            var scaleXAnimation = new DoubleAnimation()
            {
                From = 0.9,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(200)
            };
            // Set the target of the animation to be the scale transform of the control
            Storyboard.SetTarget(scaleXAnimation, ((Control)sender).RenderTransform);
            // Set the target property of the animation to be the ScaleX property
            Storyboard.SetTargetProperty(scaleXAnimation, "ScaleX");
            // Add the animation to the storyboard
            storyboard.Children.Add(scaleXAnimation);
            // Create a double animation to animate the ScaleY property from 0.9 to 1
            var scaleYAnimation = new DoubleAnimation()
            {
                From = 0.9,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(200)
            };
            // Set the target of the animation to be the scale transform of the control
            Storyboard.SetTarget(scaleYAnimation, ((Control)sender).RenderTransform);
            // Set the target property of the animation to be the ScaleY property
            Storyboard.SetTargetProperty(scaleYAnimation, "ScaleY");
            // Add the animation to the storyboard
            storyboard.Children.Add(scaleYAnimation);
            // Start the storyboard
            storyboard.Begin();
        }

        private void Button_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {

        }

        private void Button_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {

        }
    }
}
