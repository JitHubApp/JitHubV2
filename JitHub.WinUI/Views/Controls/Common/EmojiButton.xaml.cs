using JitHub.Models.LegacyGitHub;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Common
{
    public sealed partial class EmojiButton : UserControl
    {
        public static DependencyProperty ReactionProperty = DependencyProperty.Register(
            nameof(Reaction),
            typeof(ReactionType),
            typeof(EmojiButton),
            new PropertyMetadata(default(ReactionType), null)
        );

        public static DependencyProperty ReactionCommandProperty = DependencyProperty.Register(
            nameof(ReactionCommand),
            typeof(ICommand),
            typeof(EmojiButton),
            new PropertyMetadata(null));

        public static DependencyProperty ReactionCountProperty = DependencyProperty.Register(
            nameof(ReactionCount),
            typeof(int),
            typeof(EmojiButton),
            new PropertyMetadata(default(int), null));

        public static DependencyProperty ShowReactionCountProperty = DependencyProperty.Register(
            nameof(ShowReactionCount),
            typeof(bool),
            typeof(EmojiButton),
            new PropertyMetadata((bool)false, null));

        public static DependencyProperty VotedProperty = DependencyProperty.Register(
            nameof(Voted),
            typeof(bool),
            typeof (EmojiButton),
            new PropertyMetadata(default(bool), null));

        public static DependencyProperty UsersProperty = DependencyProperty.Register(
            nameof(Users),
            typeof(object),
            typeof(EmojiButton),
            new PropertyMetadata(null));



        public ReactionType Reaction
        {
            get => (ReactionType)GetValue(ReactionProperty);
            set => SetValue(ReactionProperty, value);
        }

        public int ReactionCount
        {
            get => (int)GetValue(ReactionCountProperty);
            set => SetValue(ReactionCountProperty, value);
        }

        public bool ShowReactionCount
        {
            get => (bool)GetValue(ShowReactionCountProperty);
            set { SetValue(ShowReactionCountProperty, value);}
        }

        public bool Voted
        {
            get => (bool)GetValue(VotedProperty);
            set { SetValue(VotedProperty, value);}
        }

        public ICommand ReactionCommand
        {
            get => (ICommand)GetValue(ReactionCommandProperty);
            set => SetValue(ReactionCommandProperty, value);
        }

        public IEnumerable<string> Users
        {
            get => (GetValue(UsersProperty) as IEnumerable)?.Cast<string>() ?? Enumerable.Empty<string>();
            set => SetValue(UsersProperty, value);
        }

        public EmojiButton()
        {
            this.InitializeComponent();
        }

        public Thickness GetPadding(bool showReactionCount)
        {
            return showReactionCount
                ? new Thickness(8, 0, 8, 0)
                : new Thickness(0);
        }

        public string GetAutomationId(ReactionType reaction)
        {
            return $"EmojiReactionButton_{reaction}";
        }

        public Brush? GetBackgroundBrush(bool voted)
        {
            return GetAppBrush(voted ? "AppAccentBrush" : "AppSurfaceSubtleBrush");
        }

        public Brush? GetForegroundBrush(bool voted)
        {
            return GetAppBrush(voted ? "AppAccentForegroundBrush" : "AppInkBrush");
        }

        private static Brush? GetAppBrush(string resourceKey)
        {
            return Application.Current.Resources.TryGetValue(resourceKey, out object value) && value is Brush brush
                ? brush
                : null;
        }
    }
}

