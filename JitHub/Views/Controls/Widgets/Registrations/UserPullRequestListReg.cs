using JitHub.Models.Widgets;
using JitHub.Views.Controls.Issue;
using System;
using Windows.UI.Xaml;

namespace JitHub.Views.Controls.Widgets.Registrations;

internal class UserPullRequestListReg : WidgetBase
{
    public string Type => WidgetType.UserPullRequestsList;
    public const string Name = "Pull Requests";

    public WidgetData Create()
    {
        var guid = Guid.NewGuid();
        return new WidgetData
        {
            ID = guid.ToString(),
            Type = Type,
            Name = Name,
            Size = WidgetSize.Small,
        };
    }

    public UIElement GetElement(string id)
    {
        return new UserIssueList(id, ViewModels.IssueViewModels.IssueListType.PullRequests);
    }

    public string GetName()
    {
        return Name;
    }
}
