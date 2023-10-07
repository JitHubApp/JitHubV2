using Octokit;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
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
            typeof(IEnumerable<User>),
            typeof(EmojiButton),
            new PropertyMetadata(default(IEnumerable), null));



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
            get => (IEnumerable<string>)GetValue(UsersProperty);
            set => SetValue(UsersProperty, value);
        }

        public EmojiButton()
        {
            this.InitializeComponent();
        }

        public Thickness GetPadding(bool showReactionCount)
        {
            Thickness padding;
            if (showReactionCount)
            {
                padding = new Thickness(4, 2, 4, 2);
            }
            else
            {
                padding = new Thickness(4);
            }
            return padding;
        }
    }
}
