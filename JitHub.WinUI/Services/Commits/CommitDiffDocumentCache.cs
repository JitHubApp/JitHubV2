using System;
using System.Collections.Generic;

namespace JitHub.Services;

internal sealed class CommitDiffDocumentCache
{
    private readonly int _capacity;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recency = [];
    private long _currentBytes;

    public CommitDiffDocumentCache(int capacity, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _capacity = capacity;
        _maxBytes = maxBytes;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal long CurrentBytes
    {
        get
        {
            lock (_gate)
            {
                return _currentBytes;
            }
        }
    }

    public bool TryGet(string sha, out CommitDiffDocument document)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sha, out Entry? entry))
            {
                document = CommitDiffDocument.Empty;
                return false;
            }

            _recency.Remove(entry.Node);
            _recency.AddFirst(entry.Node);
            document = entry.Document;
            return true;
        }
    }

    public bool TryStore(string sha, CommitDiffDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentNullException.ThrowIfNull(document);
        long sizeBytes = EstimateSizeBytes(document);
        if (sizeBytes > _maxBytes)
        {
            return false;
        }

        lock (_gate)
        {
            RemoveCore(sha);
            while (_entries.Count >= _capacity || _currentBytes + sizeBytes > _maxBytes)
            {
                LinkedListNode<string>? oldest = _recency.Last;
                if (oldest is null)
                {
                    break;
                }

                RemoveCore(oldest.Value);
            }

            LinkedListNode<string> node = _recency.AddFirst(sha);
            _entries[sha] = new Entry(document, sizeBytes, node);
            _currentBytes += sizeBytes;
            return true;
        }
    }

    internal static long EstimateSizeBytes(CommitDiffDocument document)
    {
        long bytes = 256;
        foreach (CommitDiffFile file in document.Files)
        {
            bytes = SaturatingAdd(bytes, 192);
            bytes = SaturatingAdd(bytes, EstimateString(file.Filename));
            bytes = SaturatingAdd(bytes, EstimateString(file.PreviousFilename));
            bytes = SaturatingAdd(bytes, EstimateString(file.Status));
            foreach (CommitDiffLine line in file.Lines)
            {
                bytes = SaturatingAdd(bytes, 80);
                bytes = SaturatingAdd(bytes, EstimateString(line.Text));
            }
        }

        foreach (CommitDiffRow row in document.Rows)
        {
            bytes = SaturatingAdd(bytes, 112);
            bytes = SaturatingAdd(bytes, EstimateString(row.Key));
            bytes = SaturatingAdd(bytes, EstimateString(row.HeaderText));
        }

        return bytes;
    }

    private void RemoveCore(string sha)
    {
        if (!_entries.Remove(sha, out Entry? existing))
        {
            return;
        }

        _recency.Remove(existing.Node);
        _currentBytes -= existing.SizeBytes;
    }

    private static long EstimateString(string? value) => value is null
        ? 0
        : 24L + (long)value.Length * sizeof(char);

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed record Entry(
        CommitDiffDocument Document,
        long SizeBytes,
        LinkedListNode<string> Node);
}
