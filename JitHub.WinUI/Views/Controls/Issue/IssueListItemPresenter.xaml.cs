using System;
using System.Collections.Generic;
using JitHub.Models.Base;
using JitHub.Models.LegacyGitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using IssueModel = JitHub.Models.LegacyGitHub.Issue;

namespace JitHub.WinUI.Views.Controls.Issue;

public sealed partial class IssueListItemPresenter : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item),
        typeof(object),
        typeof(IssueListItemPresenter),
        new PropertyMetadata(default(object), OnItemChanged));

    public object? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private RepoSelectableItemModel<IssueModel>? TypedItem => Item as RepoSelectableItemModel<IssueModel>;

    public object? State => TypedItem?.Model.State;
    public string Title => TypedItem?.Model.Title ?? string.Empty;
    public List<Label> Labels => TypedItem?.Model.Labels ?? [];
    public int Number => TypedItem?.Model.Number ?? 0;
    public DateTimeOffset CreatedAt => TypedItem?.Model.CreatedAt ?? default;
    public User? User => TypedItem?.Model.User;
    public string NumberText => Number > 0 ? $"#{Number}" : "#0";
    public string CreatedAtText => TypedItem is null ? string.Empty : $"opened on {CreatedAt:MMM dd, yyyy}";
    public string UserText => string.IsNullOrWhiteSpace(User?.Login) ? string.Empty : $"by {User.Login}";

    public IssueListItemPresenter()
    {
        InitializeComponent();
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is IssueListItemPresenter self)
        {
            self.Bindings.Update();
        }
    }
}
