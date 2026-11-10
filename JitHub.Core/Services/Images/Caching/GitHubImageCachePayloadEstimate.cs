using System;

namespace JitHub.Core.Services.Images.Caching;

/// <summary>
/// A running estimate of the image cache's payload bytes, so a write can
/// decide whether to enforce the soft cap without scanning the directory.
/// A negative value means unknown; the next read recalculates it.
/// </summary>
internal sealed class GitHubImageCachePayloadEstimate
{
    private readonly object _gate = new();
    private readonly string _rootPath;
    private long _bytes = -1;

    internal GitHubImageCachePayloadEstimate(string rootPath)
    {
        _rootPath = rootPath;
    }

    internal long GetOrInitialize()
    {
        lock (_gate)
        {
            if (_bytes < 0)
            {
                _bytes = GitHubImageCacheFiles.CalculatePayloadBytes(_rootPath);
            }

            return _bytes;
        }
    }

    internal long Current
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    internal void Add(long delta)
    {
        lock (_gate)
        {
            if (_bytes < 0)
            {
                return;
            }

            if (delta > 0 && _bytes > long.MaxValue - delta)
            {
                _bytes = long.MaxValue;
                return;
            }

            _bytes = Math.Max(0, _bytes + delta);
        }
    }

    internal void Set(long value)
    {
        lock (_gate)
        {
            _bytes = value;
        }
    }

    internal void Invalidate() => Set(-1);
}
