using System.Collections.Generic;

namespace JitHub.Services;

public sealed record PagedGitHubItems<T>(IReadOnlyList<T> Items, bool HasMoreItems);
