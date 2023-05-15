#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;
using Windows.Foundation;

namespace WebView2Ex.UI;

partial class WebView2Ex : ICoreDropOperationTarget
{
    //LinkedListNode<ICoreDropOperationTarget>? thisNode;
    //void RegisterDragDropEvents()
    //{
    //    thisNode = DragDropService.Register(this);
    //}

    //void UnregisterDragDropEvents()
    //{
    //    DragDropService.Unregister(thisNode);
    //    thisNode = null;
    //}

    void RegisterDragDropEvents()
    {
        var manager = CoreDragDropManager.GetForCurrentView();
        manager.TargetRequested += TargetRequested;
    }

    void UnregisterDragDropEvents()
    {
        var manager = CoreDragDropManager.GetForCurrentView();
        manager.TargetRequested -= TargetRequested;
    }


    private void TargetRequested(CoreDragDropManager sender, CoreDropOperationTargetRequestedEventArgs args)
    {
        args.SetTarget(this);
    }

    IAsyncOperation<DataPackageOperation> ICoreDropOperationTarget.EnterAsync(CoreDragInfo dragInfo, CoreDragUIOverride dragUIOverride)
    {
        if (Controller is null) return Task.FromResult(DataPackageOperation.None).AsAsyncOperation();
        return Task.FromResult(Controller.DragEnter(dragInfo, dragUIOverride)).AsAsyncOperation();
    }

    IAsyncOperation<DataPackageOperation> ICoreDropOperationTarget.OverAsync(CoreDragInfo dragInfo, CoreDragUIOverride dragUIOverride)
    {
        if (Controller is null) return Task.FromResult(DataPackageOperation.None).AsAsyncOperation();
        return Task.FromResult(Controller.DragOver(dragInfo, dragUIOverride)).AsAsyncOperation();
    }

    IAsyncAction ICoreDropOperationTarget.LeaveAsync(CoreDragInfo dragInfo)
    {
        if (Controller is null) return Task.CompletedTask.AsAsyncAction();
        Controller.DragLeave();
        return Task.CompletedTask.AsAsyncAction();
    }

    IAsyncOperation<DataPackageOperation> ICoreDropOperationTarget.DropAsync(CoreDragInfo dragInfo)
    {
        return DropAsync(dragInfo).AsAsyncOperation();
    }
    async Task<DataPackageOperation> DropAsync(CoreDragInfo dragInfo)
    {
        if (Controller is null) return DataPackageOperation.None;
        var operation = Controller.Drop(dragInfo);
        dragInfo.Data.ReportOperationCompleted(operation);
        var formats = dragInfo.Data.AvailableFormats.ToArray();
        return operation;
    }
}
