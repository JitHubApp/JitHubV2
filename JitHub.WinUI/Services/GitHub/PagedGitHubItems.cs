using System.Collections.Generic;

namespace JitHub.WinUI.Services.GitHub;

public sealed record PagedGitHubItems<T>(IReadOnlyList<T> Items, bool HasMoreItems);
