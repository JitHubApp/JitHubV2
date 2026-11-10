using System;
using JitHub.Models.CodeViewer;

namespace JitHub.Services.CodeViewer;

public interface IFilePreviewResolver
{
    FilePreviewDescriptor Resolve(string path, long byteSize, ReadOnlyMemory<byte> headSample);
}
