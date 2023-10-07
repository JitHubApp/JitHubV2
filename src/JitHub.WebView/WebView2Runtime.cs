#nullable enable
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;
using WebView2Ex.Natives;
using Windows.Globalization;
using Windows.UI.Composition;
using Windows.UI.Core;
using Windows.UI.WindowManagement;
using Windows.Win32.Foundation;

namespace WebView2Ex;

public static class WebView2Environment
{
    public static async ValueTask<CoreWebView2Environment> CreateAsync(CoreWebView2EnvironmentOptions? options = null)
    {
        string browserInstall = "";
        string userDataFolder = "";
        if (options is null)
        {
            options = new CoreWebView2EnvironmentOptions();
            var applicationLanguagesList = ApplicationLanguages.Languages;
            if (applicationLanguagesList.Count > 0)
            {
                options.Language = applicationLanguagesList[0];
            }
        }
        
        return await CoreWebView2Environment.CreateWithOptionsAsync(
            browserInstall,
            userDataFolder,
            options
        );
    }
}
public class WebView2Runtime : IDisposable
{
    public CoreWebView2CompositionController? CompositionController { get; private set; }
    public CoreWebView2? CoreWebView2 { get; private set; }
    public CoreWebView2Environment? Environment { get; private set; }
    internal UI.WebView2Ex? Owner;
    Visual? buffer;
    internal Visual? RootVisualTarget
    {
        get => buffer;
        set
        {
            if (CompositionController is not null)
                CompositionController.RootVisualTarget = buffer = value;
        }
    }
    private WebView2Runtime(
        CoreWebView2CompositionController CompositionController)
    {
        this.CompositionController = CompositionController;
        CoreWebView2 = CompositionController.CoreWebView2;
        Environment = CoreWebView2.Environment;
    }
    public async static Task<WebView2Runtime> CreateAsync(CoreWebView2Environment env)
    {
        var windowRef = CoreWebView2ControllerWindowReference.CreateFromCoreWindow(CoreWindow.GetForCurrentThread());
        var controller = await env.CreateCoreWebView2CompositionControllerAsync(windowRef);
        controller.ShouldDetectMonitorScaleChanges = false;
        return new(controller);
    }
    public async static Task<WebView2Runtime> CreateAsync()
        => await CreateAsync(await WebView2Environment.CreateAsync(null));

    internal void SetWindow(HWND window)
    {
        if (CompositionController is not null)
            CompositionController.ParentWindow = CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)window.Value);
    }
    internal void SetWindow(AppWindow appWindow)
    {
        var interop = (IApplicationWindow_HwndInterop)(dynamic)appWindow;
        SetWindow((HWND)(nint)interop.WindowHandle.Value);
    }
    internal void SetWindow(CoreWindow coreWindow)
    {
        if (CompositionController is not null)
            CompositionController.ParentWindow = CoreWebView2ControllerWindowReference.CreateFromCoreWindow(coreWindow);
    }

    public void Dispose()
    {
        CompositionController?.Close();
        CompositionController = null;
        Environment = null;
        CoreWebView2 = null;
    }
}
