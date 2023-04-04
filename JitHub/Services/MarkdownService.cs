using Markdig;
using Windows.UI.Xaml;

namespace JitHub.Services
{
    public class MarkdownService : IMarkdownService
    {
        private MarkdownPipeline _pipeline;
        public MarkdownService()
        {
            _pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        }
        public string ParseGFM(string gfm, ApplicationTheme theme = ApplicationTheme.Light)
        {
            var html = Markdown.ToHtml(gfm, _pipeline);
            var color = theme == ApplicationTheme.Light ? "black" : "white";
            return $@"
                <div id='container'>
                    <style>
                        body {{
                            margin: 0;
                            overflow: hidden;
                            font-family: Segoe UI,Frutiger,Frutiger Linotype,Dejavu Sans,Helvetica Neue,Arial,sans-serif;
                        }}
                        * {{
                            color: {color};
                        }}
                    </style>
                    {html}
                </div>";
        }
    }
}
