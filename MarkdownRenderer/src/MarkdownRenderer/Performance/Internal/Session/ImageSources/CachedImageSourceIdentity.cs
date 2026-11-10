namespace MarkdownRenderer.Performance.Internal.Session.ImageSources;

/// <summary>
/// A scalar snapshot of a cache entry, safe to hold across an admission wait
/// because it retains neither the entry nor its bytes.
/// </summary>
internal readonly record struct CachedImageSourceIdentity(long EntryIdentity, int ByteLength);
