using MarkdownRenderer;

const string source = "# Native Markdown\n\n- [x] accessible\n\n| Pack | State |\n|---|---|\n| Core | lean |\n\n<span>literal</span>";

var engine = new MarkdownEngineBuilder()
    .UseProfile(MarkdownProfiles.GfmStrict)
    .WithParseCacheBudgetBytes(256 * 1024)
    .Build();

var first = await engine.ParseAsync(source).ConfigureAwait(false);
var second = await engine.ParseAsync(source).ConfigureAwait(false);

if (!ReferenceEquals(first, second) ||
    first.Source != source ||
    first.GetHeadings().Count != 1 ||
    first.HasErrors)
{
    return 1;
}

Console.WriteLine($"{engine.Profile.Id}: {first.SourceMap.Count} mapped semantic elements");
return 0;
