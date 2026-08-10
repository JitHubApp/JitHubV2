# JitHub vNext Route Ownership

JitHub has one production owner for every route. New behavior belongs in the owner below; retired list/detail pages and view models must not be reintroduced.

| Route | Page | View model |
| --- | --- | --- |
| Application shell | `Views.Pages.ShellPage` | `ShellPageViewModel` |
| Sign in | `Views.Pages.LoginPage` | `LoginPageViewModel` |
| Home | `DashboardPage` | `DashboardPageViewModel` |
| My Issues | `MyIssuesPage` | `MyIssuesPageViewModel` |
| My Pull Requests | `MyPullRequestsPage` | `MyPullRequestsPageViewModel` |
| Stars | `StarsPage` | `StarLibraryPageViewModel` |
| Gists | `GistsPage` | `GistsPageViewModel` |
| Notifications | `NotificationsPage` | `NotificationsPageViewModel` |
| Profile | `ProfilePage` | `ProfilePageViewModel` |
| Repositories | `RepoManagePage` | `RepoManagePageViewModel` |
| Repository workspace | `RepoDetailPage` | `RepoDetailViewModel` |
| Repository code | `RepoCodePage` | `RepoCodePageViewModel` |
| Repository issues | `RepoIssuePage` | `RepoIssuePageViewModel` |
| Repository pull requests | `RepoPullRequestPage` | `RepoPullRequestPageViewModel` |
| Repository commits | `RepoCommitsPage` | `RepoCommitsPageViewModel` |
| Repository search | `RepoSearchResultPage` | `RepoSearchResultPageViewModel` |
| Settings | `SettingsPage` | `SettingsPageViewModel` |

## Developer Surfaces

`DesignLabPage` and `DevConsole` are not production routes. Shell commands are listed and executable only when Developer Mode is enabled, and both execution boundaries use `DeveloperRoutePolicy`. The Design Lab may also be activated by the isolated automation launch path, which uses separate temporary local and cache roots. Dev Console never has a launch override.

## Retired Surfaces

The legacy issue detail frame, pull-request conversation/commits pages, commit detail frame, their converters, and their view models were removed after parity moved into the canonical workspaces. The old login, settings, dashboard, repository-sidebar/manage, My Issues, route-button, pull-request creation, and text-diff dependency islands were also removed after their behavior moved to the owners above.

Production navigation must not construct an alternate shell, search view model, repository workspace view model, or list/detail stack. New repository, issue, pull-request, and commit behavior belongs in the canonical pages and services named in this map.
