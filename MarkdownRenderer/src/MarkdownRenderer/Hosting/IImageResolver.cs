using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using Windows.Foundation;

namespace MarkdownRenderer.Hosting;

/// <summary>
/// Stable image-resolution service. It deliberately inherits the 0.x contract
/// so existing controls and resolvers remain source compatible.
/// </summary>
public interface IImageResolver : IMarkdownImageResolver
{
}
