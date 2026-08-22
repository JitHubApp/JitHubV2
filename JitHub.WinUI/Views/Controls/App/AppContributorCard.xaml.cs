using System;
using JitHub.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.App;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class AppContributorCard : UserControl
{
    public AppContributorCard()
    {
        InitializeComponent();
    }

    internal AppContributorCard(CreditPersonale contributor) : this()
    {
        ArgumentNullException.ThrowIfNull(contributor);
        AutomationProperties.SetAutomationId(ContributorRoot, contributor.AutomationId);
        AutomationProperties.SetName(ContributorRoot, contributor.AccessibleName);
        AutomationProperties.SetName(ContributorPicture, contributor.PersonaleName);
        ContributorPicture.Source = contributor.ImageSource;
        ContributorNameText.Text = contributor.PersonaleName;
        ContributorRoleText.Text = contributor.Role;
        ContributorDescriptionText.Text = contributor.Description;
        PopulateLinks(contributor.Links);
    }

    private void PopulateLinks(System.Collections.Generic.IEnumerable<PersonalLink> links)
    {
        foreach (PersonalLink link in links)
        {
            var logo = new Image
            {
                Width = 18,
                Height = 18,
                Source = link.LogoSource
            };
            var logoSurface = new Border
            {
                Padding = new Thickness(2),
                Background = link.LogoBackgroundBrush,
                CornerRadius = (CornerRadius)Application.Current.Resources["AppRadiusTight"],
                Child = logo
            };
            var button = new HyperlinkButton
            {
                Margin = new Thickness(0, 2, 8, 0),
                Padding = new Thickness(0),
                NavigateUri = new Uri(link.Link, UriKind.Absolute),
                Content = logoSurface
            };
            AutomationProperties.SetAutomationId(button, link.AutomationId);
            AutomationProperties.SetName(button, link.AccessibleName);
            LinksPanel.Children.Add(button);
        }
    }
}
