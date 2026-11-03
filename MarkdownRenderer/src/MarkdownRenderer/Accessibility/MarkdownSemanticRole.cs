using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility;

internal enum MarkdownSemanticRole
{
    Document,
    Group,
    Paragraph,
    Heading,
    CodeBlock,
    Link,
    List,
    ListItem,
    Table,
    TableCell,
    Image,
    Math,
    Diagram,
    Embed,
    Abbreviation,
}
