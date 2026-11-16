namespace MarkdownRenderer.Performance.Internal.Scheduling;

/// <summary>Lifecycle of a <see cref="MarkdownImageActivation"/> in its scheduler.</summary>
internal enum MarkdownImageActivationState : byte { Created, Queued, Waiting, Running, Completed }
