using System;
using System.ComponentModel;
using JitHub.Models;
using JitHub.Models.Base;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Common
{
    public sealed partial class IssueSidePanelSelectableItem : UserControl
    {
        public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
            nameof(Item),
            typeof(SelectableItem),
            typeof(IssueSidePanelSelectableItem),
            new PropertyMetadata(default(SelectableItem), OnItemChanged));

        private CheckBox? _checkBox;
        private SelectableItem? _subscribedItem;
        private bool _syncingCheckState;

        public SelectableItem? Item
        {
            get => (SelectableItem?)GetValue(ItemProperty);
            set => SetValue(ItemProperty, value);
        }

        public IssueSidePanelSelectableItem()
        {
            InitializeComponent();
            Unloaded += OnUnloaded;
        }

        private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is IssueSidePanelSelectableItem self)
            {
                self.SubscribeToItem(e.NewValue as SelectableItem);
                self.RenderItem();
            }
        }

        private void SubscribeToItem(SelectableItem? item)
        {
            if (_subscribedItem is not null)
            {
                _subscribedItem.PropertyChanged -= OnItemPropertyChanged;
            }

            _subscribedItem = item;

            if (_subscribedItem is not null)
            {
                _subscribedItem.PropertyChanged += OnItemPropertyChanged;
            }
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SelectableItem.Selected))
            {
                SyncCheckState();
            }
            else if (e.PropertyName is nameof(SelectableItem.Selectable))
            {
                RenderItem();
            }
        }

        private void RenderItem()
        {
            _checkBox = null;

            if (Item is null)
            {
                Presenter.Content = null;
                return;
            }

            var content = CreateContent(Item);
            if (!Item.Selectable)
            {
                Presenter.Content = content;
                return;
            }

            var checkBox = new CheckBox
            {
                Content = content,
                MinHeight = 32,
                VerticalAlignment = VerticalAlignment.Center
            };

            _checkBox = checkBox;
            SyncCheckState();

            checkBox.Checked += OnCheckedChanged;
            checkBox.Unchecked += OnCheckedChanged;
            Presenter.Content = checkBox;
        }

        private static FrameworkElement CreateContent(SelectableItem item)
            => item switch
            {
                SelectableLabel selectableLabel => new RepoLabel
                {
                    Label = selectableLabel.Label,
                    VerticalAlignment = VerticalAlignment.Center
                },
                SelectableUser selectableUser => new Avatar
                {
                    Login = selectableUser.Login,
                    Url = selectableUser.AvatarUrl,
                    ShowLogin = true,
                    Size = UISize.MEDIUM,
                    VerticalAlignment = VerticalAlignment.Center
                },
                _ => new TextBlock
                {
                    Text = item.Type,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

        private void SyncCheckState()
        {
            if (_checkBox is null || Item is null)
            {
                return;
            }

            _syncingCheckState = true;
            _checkBox.IsChecked = Item.Selected;
            _syncingCheckState = false;
        }

        private void OnCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingCheckState || Item is null || sender is not CheckBox checkBox)
            {
                return;
            }

            Item.Selected = checkBox.IsChecked == true;
            var command = Item.SelectionCommand;
            if (command?.CanExecute(Item) == true)
            {
                command.Execute(Item);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            SubscribeToItem(null);

            if (_checkBox is not null)
            {
                _checkBox.Checked -= OnCheckedChanged;
                _checkBox.Unchecked -= OnCheckedChanged;
            }
        }
    }
}
