using System;
using System.Threading;

namespace MarkdownRenderer.Layout.Internal.Vectors.Drawing;

internal sealed class VectorNativeResourceCredit : IDisposable
{
    private VectorNativeResourceAdmission? _owner;
    private readonly long _weightUnits;

    internal VectorNativeResourceCredit(VectorNativeResourceAdmission owner, long weightUnits)
    {
        _owner = owner;
        _weightUnits = weightUnits;
    }

    internal long WeightUnits => _weightUnits;

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Refund(_weightUnits);
}
