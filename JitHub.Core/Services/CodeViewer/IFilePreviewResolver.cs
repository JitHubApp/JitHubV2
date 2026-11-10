using System;
using JitHub.Core.Models.CodeViewer;

namespace JitHub.Core.Services.CodeViewer;

public interface IFilePreviewResolver
{
    FilePreviewDescriptor Resolve(string path, long byteSize, ReadOnlyMemory<byte> headSample);
}
