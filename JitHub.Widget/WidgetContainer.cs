using Windows.UI.Xaml.Controls;

// The Templated Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234235

namespace JitHub.Widget
{
    public sealed class WidgetContainer : Control
    {
        private Grid _header;
        public WidgetContainer()
        {
            this.DefaultStyleKey = typeof(WidgetContainer);
        }

        protected override void OnApplyTemplate()
        {
            this._header = GetTemplateChild("Header") as Grid;
        }
    }
}
