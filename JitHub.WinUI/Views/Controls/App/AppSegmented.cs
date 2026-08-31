using System.Collections.Generic;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.App;

public sealed partial class AppSegmented : Segmented
{
    protected override DependencyObject GetContainerForItemOverride() => new AppSegmentedItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is AppSegmentedItem;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new AppSegmentedAutomationPeer(this);
}

public sealed partial class AppSegmentedItem : SegmentedItem
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new AppSegmentedItemAutomationPeer(this);
}

internal sealed partial class AppSegmentedAutomationPeer : ListViewBaseAutomationPeer
{
    private readonly AppSegmented _owner;

    public AppSegmentedAutomationPeer(AppSegmented owner) : base(owner)
    {
        _owner = owner;
    }

    protected override string GetClassNameCore() => nameof(AppSegmented);

    protected override IList<AutomationPeer> GetChildrenCore()
    {
        List<AutomationPeer> children = new(_owner.Items.Count);
        for (int index = 0; index < _owner.Items.Count; index++)
        {
            if (_owner.ContainerFromIndex(index) is not AppSegmentedItem { Visibility: Visibility.Visible } item)
            {
                continue;
            }

            AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(item) ??
                FrameworkElementAutomationPeer.CreatePeerForElement(item);
            if (peer is not null)
            {
                children.Add(peer);
            }
        }

        return children;
    }
}

internal sealed partial class AppSegmentedItemAutomationPeer : ListViewItemAutomationPeer, ISelectionItemProvider
{
    private readonly AppSegmentedItem _owner;

    public AppSegmentedItemAutomationPeer(AppSegmentedItem owner) : base(owner)
    {
        _owner = owner;
    }

    protected override string GetClassNameCore() => nameof(AppSegmentedItem);

    protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.SelectionItem => this,
        _ => base.GetPatternCore(patternInterface),
    };

    bool ISelectionItemProvider.IsSelected => _owner.IsSelected;

    IRawElementProviderSimple ISelectionItemProvider.SelectionContainer
    {
        get
        {
            AppSegmented? segmented = ItemsControl.ItemsControlFromItemContainer(_owner) as AppSegmented;
            AutomationPeer? peer = segmented is null
                ? null
                : FrameworkElementAutomationPeer.FromElement(segmented) ??
                  FrameworkElementAutomationPeer.CreatePeerForElement(segmented);
            return peer is null ? null! : ProviderFromPeer(peer);
        }
    }

    void ISelectionItemProvider.AddToSelection() => Select();

    void ISelectionItemProvider.RemoveFromSelection()
    {
        // A segmented control always keeps one active view.
    }

    void ISelectionItemProvider.Select() => Select();

    private void Select()
    {
        _owner.IsSelected = true;
    }
}
