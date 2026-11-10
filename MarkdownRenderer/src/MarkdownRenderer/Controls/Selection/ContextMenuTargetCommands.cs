using System;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// The context menu items for the link, image, code block or table under the
/// pointer, and the commands they run. A host command for the same kind
/// replaces the built-in clipboard write.
/// </summary>
/// <remarks>
/// Each item's Tag holds its command and a lease on the captured target. The
/// lease is disposed when the menu closes; a click runs on a clone, so a
/// command that outlives the menu keeps its own lease.
/// </remarks>
internal sealed class ContextMenuTargetCommands
{
    private readonly ISelectionContextMenuHost _host;
    private readonly Func<MenuFlyout, bool> _isCurrentMenu;

    /// <param name="host">The renderer services the commands call.</param>
    /// <param name="isCurrentMenu">
    /// True while <c>menu</c> is still the open menu. Command providers can
    /// reenter the renderer and close it while items are being added.
    /// </param>
    internal ContextMenuTargetCommands(
        ISelectionContextMenuHost host,
        Func<MenuFlyout, bool> isCurrentMenu)
    {
        _host = host;
        _isCurrentMenu = isCurrentMenu;
    }

    /// <summary>
    /// Adds an item for each command <paramref name="target"/> supports.
    /// Returns true when at least one item was added.
    /// </summary>
    internal bool AddItems(MenuFlyout menu, ContextMenuTarget target)
    {
        bool added = false;
        if (!string.IsNullOrWhiteSpace(target.LinkUrl))
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyLink,
                target.LinkSourceRange,
                target.LinkUrl);
            bool isEnabled = CanExecuteOrDefault(action, hasDefault: true);
            if (!_isCurrentMenu(menu))
                return added;
            AddItem(menu, action, target, isEnabled, MarkdownStringKeys.CopyLink,
                static () => MarkdownLocalizedStrings.ContextMenuCopyLink, "MarkdownContextCopyLink");
            added = true;
            if (!_isCurrentMenu(menu))
                return added;
        }

        if (target.ImageUrl is not null || target.ImageBitmapLease is not null)
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyImage,
                target.ImageSourceRange,
                target.ImageUrl);
            bool isEnabled = CanExecuteOrDefault(
                action,
                hasDefault: !string.IsNullOrWhiteSpace(target.ImageUrl) ||
                            target.ImageBitmapLease is not null);
            if (!_isCurrentMenu(menu))
                return added;
            AddItem(menu, action, target, isEnabled, MarkdownStringKeys.CopyImage,
                static () => MarkdownLocalizedStrings.ContextMenuCopyImage, "MarkdownContextCopyImage");
            added = true;
            if (!_isCurrentMenu(menu))
                return added;
        }

        if (target.CodeText is not null)
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyCode,
                target.CodeSourceRange,
                Language: target.CodeLanguage);
            bool isEnabled = CanExecuteOrDefault(
                action,
                hasDefault: target.CodeText.Length > 0);
            if (!_isCurrentMenu(menu))
                return added;
            AddItem(menu, action, target, isEnabled, MarkdownStringKeys.CopyCode,
                static () => MarkdownLocalizedStrings.ContextMenuCopyCode, "MarkdownContextCopyCode");
            added = true;
            if (!_isCurrentMenu(menu))
                return added;
        }

        if (target.TableSnapshot is { } tableSnapshot)
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyTable,
                target.TableSourceRange);
            bool isEnabled = CanExecuteOrDefault(
                action,
                hasDefault: tableSnapshot.HasText);
            if (!_isCurrentMenu(menu))
                return added;
            AddItem(menu, action, target, isEnabled, MarkdownStringKeys.CopyTable,
                static () => MarkdownLocalizedStrings.ContextMenuCopyTable, "MarkdownContextCopyTable");
            added = true;
        }

        return added;
    }

    // Resolves the item's text after it is constructed, as the inline
    // initializers did, then attaches its command and adds it.
    private void AddItem(
        MenuFlyout menu,
        MarkdownTargetCommand action,
        ContextMenuTarget target,
        bool isEnabled,
        string textKey,
        Func<string> fallbackText,
        string automationId)
    {
        var item = new MenuFlyoutItem
        {
            Text = _host.ResolveLocalizedString(
                textKey,
                fallbackText()),
            IsEnabled = isEnabled,
        };
        AutomationProperties.SetAutomationId(item, automationId);
        Attach(item, action, target);
        menu.Items.Add(item);
    }

    /// <summary>Detaches the target items of <paramref name="menu"/> and disposes their target leases.</summary>
    internal void Release(MenuFlyout menu)
    {
        foreach (object? entry in menu.Items)
        {
            if (entry is MenuFlyoutItem item &&
                item.Tag is ValueTuple<MarkdownTargetCommand, ContextMenuTarget>)
            {
                var command = (ValueTuple<MarkdownTargetCommand, ContextMenuTarget>)item.Tag;
                item.Click -= OnItemClick;
                item.Tag = null;
                ContextMenuTargets.DisposeContextMenuTarget(command.Item2);
            }
        }
    }

    private void Attach(
        MenuFlyoutItem item,
        MarkdownTargetCommand action,
        ContextMenuTarget target)
    {
        item.Tag = (action, target);
        item.Click += OnItemClick;
    }

    private async void OnItemClick(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem item ||
            item.Tag is not ValueTuple<MarkdownTargetCommand, ContextMenuTarget> command)
        {
            return;
        }

        try
        {
            using ContextMenuTarget executionTarget = command.Item2.CloneForExecution();
            await ExecuteOrDefaultAsync(command.Item1, executionTarget).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Context-menu command failed: {ex.Message}");
        }
    }

    private bool CanExecuteOrDefault(
        MarkdownTargetCommand action,
        bool hasDefault)
    {
        try
        {
            bool canExecute = MarkdownTargetCommandDispatcher.CanExecute(
                _host.CommandProvider,
                action,
                out bool isProvided);
            return isProvided ? canExecute : hasDefault;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{action.Kind}' query failed: {ex.Message}");
            return hasDefault;
        }
    }

    private async Task<bool> ExecuteOrDefaultAsync(
        MarkdownTargetCommand action,
        ContextMenuTarget target)
    {
        try
        {
            MarkdownTargetCommandDispatchResult dispatch =
                MarkdownTargetCommandDispatcher.Execute(_host.CommandProvider, action);
            if (dispatch == MarkdownTargetCommandDispatchResult.Executed)
                return true;
            if (dispatch == MarkdownTargetCommandDispatchResult.Disabled)
                return false;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{action.Kind}' failed: {ex.Message}");
        }

        return action.Kind switch
        {
            MarkdownCommandKind.CopyLink => SelectionClipboard.CopyTextTargetToClipboard(action.Target),
            MarkdownCommandKind.CopyImage when target.ImageUrl is not null ||
                                               target.ImageBitmapLease is not null =>
                await SelectionClipboard.CopyImageTargetToClipboardAsync(
                    target.ImageUrl,
                    target.ImageBitmapLease?.Bitmap).ConfigureAwait(true),
            MarkdownCommandKind.CopyCode when target.CodeText is not null =>
                CopyCode(target.CodeText),
            MarkdownCommandKind.CopyTable when target.TableSnapshot is { } tableSnapshot =>
                await SelectionClipboard.CopyTableTargetToClipboardAsync(tableSnapshot).ConfigureAwait(true),
            _ => false,
        };
    }

    private bool CopyCode(string codeText)
    {
        bool succeeded = SelectionClipboard.CopyTextTargetToClipboard(codeText);
        _host.RaiseCopyCompleted(MarkdownCopyKind.CodeBlock, succeeded);
        return succeeded;
    }
}
