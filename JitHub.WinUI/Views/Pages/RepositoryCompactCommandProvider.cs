using System;
using System.Collections.Generic;

namespace JitHub.WinUI.Views.Pages;

public sealed record RepositoryCompactCommand(
    string Id,
    string Label,
    Action Execute,
    bool IsEnabled = true);

public interface IRepositoryCompactCommandProvider
{
    IReadOnlyList<RepositoryCompactCommand> GetRepositoryCompactCommands();
}
