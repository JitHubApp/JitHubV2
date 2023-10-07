using System.Numerics;
using Windows.UI.Xaml;

namespace WebView2Ex.UI;

partial class WebView2Ex
{
    void DisconnectFromRootVisualTarget()
    {
        var CompositionController = this.Controller;
        if (CompositionController is not null)
        {
            CompositionController.RootVisualTarget = null;
        }
    }

    void SetCoreWebViewAndVisualSize(float width, float height)
    {
        var CoreWebView2 = this.CoreWebView2;
        if (CoreWebView2 == null && visual == null) return;

        if (CoreWebView2 != null)
        {
            CheckAndUpdateWebViewPosition();
        }

        // The CoreWebView2 visuals hosted under the bridge visual are already scaled for the rasterization scale.
        // To keep them from being scaled again from the scale above the WebView2 element, we need to apply
        // an inverse scale on the bridge visual. Since the inverse scale will reduce the size of the bridge visual, we
        // need to scale up the size by the rasterization scale to compensate.

        if (visual != null)
        {
            float m_rasterizationScale = (float)this.rasterizationScale;
            Vector2 newSize = new(width * m_rasterizationScale, height * m_rasterizationScale);
            Vector3 newScale = new(1.0f / m_rasterizationScale, 1.0f / m_rasterizationScale, 1.0f);

            visual.Size = newSize;
            visual.Scale = newScale;
        }
    }
}
