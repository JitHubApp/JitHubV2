using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;
using Windows.Foundation;
using Windows.UI.Xaml;

namespace WebView2Ex
{
    class DragDropService// : ICoreDropOperationTarget
    {
        //static LinkedList<ICoreDropOperationTarget> Registered = new();
        //public static LinkedListNode<ICoreDropOperationTarget> Register(ICoreDropOperationTarget coreDropOperationTarget)
        //    => Registered.AddLast(coreDropOperationTarget);
        //public static void Unregister(LinkedListNode<ICoreDropOperationTarget> coreDropOperationTarget)
        //    => Registered.Remove(coreDropOperationTarget);
        //static CoreDragDropManager CpreDragDropManager = CoreDragDropManager.GetForCurrentView();
        //static DragDropService()
        //{
        //    CpreDragDropManager.TargetRequested += TargetRequested;
        //}
        //static DragDropService Singleton = new();
        //private DragDropService() { }

        //private static void TargetRequested(CoreDragDropManager sender, CoreDropOperationTargetRequestedEventArgs args)
        //{
        //    args.SetTarget(Singleton);
        //}

        //IAsyncOperation<DataPackageOperation> ICoreDropOperationTarget.EnterAsync(CoreDragInfo dragInfo, CoreDragUIOverride dragUIOverride)
        //{
        //    return Task.Run(async delegate
        //    {
        //        DataPackageOperation result = DataPackageOperation.None;
        //        foreach (var target in Registered)
        //        {
        //            var indiresult = await target.EnterAsync(dragInfo, dragUIOverride);
        //            if (indiresult != DataPackageOperation.None)
        //                result = indiresult;
        //        }
        //        return result;
        //    }).AsAsyncOperation();
        //}

        //IAsyncOperation<DataPackageOperation> ICoreDropOperationTarget.OverAsync(CoreDragInfo dragInfo, CoreDragUIOverride dragUIOverride)
        //{
        //    return Task.Run(async delegate
        //    {
        //        DataPackageOperation result = DataPackageOperation.None;
        //        foreach (var target in Registered)
        //        {
        //            var indiresult = await target.OverAsync(dragInfo, dragUIOverride);
        //            if (indiresult != DataPackageOperation.None)
        //                result = indiresult;
        //        }
        //        return result;
        //    }).AsAsyncOperation();
        //}

        //IAsyncAction ICoreDropOperationTarget.LeaveAsync(CoreDragInfo dragInfo)
        //{
        //    return Task.Run(async delegate
        //    {
        //        foreach (var target in Registered)
        //        {
        //            await target.LeaveAsync(dragInfo);
        //        }
        //    }).AsAsyncAction();
        //}

        //IAsyncOperation<DataPackageOperation> ICoreDropOperationTarget.DropAsync(CoreDragInfo dragInfo)
        //{
        //    return Task.Run(async delegate
        //    {
        //        DataPackageOperation result = DataPackageOperation.None;
        //        foreach (var target in Registered)
        //        {
        //            var indiresult = await target.DropAsync(dragInfo);
        //            if (indiresult != DataPackageOperation.None)
        //                result = indiresult;
        //        }
        //        return result;
        //    }).AsAsyncOperation();
        //}
    }
}
