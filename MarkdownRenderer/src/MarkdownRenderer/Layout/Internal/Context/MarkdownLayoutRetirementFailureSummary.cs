using System;

namespace MarkdownRenderer.Layout.Internal.Context;

/// <summary>Privacy-safe bounded summary of a failed retirement operation.</summary>
internal sealed class MarkdownLayoutRetirementFailureSummary : Exception
{
    internal MarkdownLayoutRetirementFailureSummary(Exception exception)
        : base(CreateMessage(exception))
    {
        ExceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        HResultCode = exception.HResult;
    }

    internal MarkdownLayoutRetirementFailureSummary(int omittedFailureCount)
        : base($"{omittedFailureCount} additional layout retirement failures omitted.")
    {
        OmittedFailureCount = omittedFailureCount;
    }

    internal readonly string? ExceptionType;
    internal readonly int? HResultCode;
    internal readonly int? OmittedFailureCount;

    private static string CreateMessage(Exception exception)
    {
        string typeName = exception.GetType().FullName ?? exception.GetType().Name;
        return $"Layout retirement failed: {typeName} (HResult=0x{exception.HResult:X8}).";
    }
}
