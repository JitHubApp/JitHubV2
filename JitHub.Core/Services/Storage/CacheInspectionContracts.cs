using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JitHub.Services;

public enum CacheOwnerHealth
{
    Healthy,
    Degraded,
    Unhealthy,
    Unavailable
}

public static class CacheMetricKeys
{
    public const string DatabasePhysicalBytes = "database_physical_bytes";
    public const string PayloadDirectoryPhysicalBytes = "payload_directory_physical_bytes";
    public const string LogicalMetadataBytes = "logical_metadata_bytes";
    public const string LogicalPayloadBytes = "logical_payload_bytes";
    public const string ActivePayloadBytes = "active_payload_bytes";
    public const string ManifestBytes = "manifest_bytes";
    public const string OrphanBytes = "orphan_bytes";
    public const string SchemaVersion = "schema_version";
    public const string DatabaseExists = "database_exists";
    public const string RecoveryJournalPhysicalBytes = "recovery_journal_physical_bytes";
    public const string RecoveryEntryCount = "recovery_entry_count";
}

public static class CacheInspectionDetail
{
    private const int MaximumMessages = 8;

    public static string? Format(IEnumerable<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        string[] distinct = messages
            .Where(static message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinct.Length == 0)
        {
            return null;
        }

        string detail = string.Join(" ", distinct.Take(MaximumMessages));
        return distinct.Length <= MaximumMessages
            ? detail
            : $"{detail} {distinct.Length - MaximumMessages} additional problem(s) were found.";
    }
}

public sealed record CacheStoreInspection(
    CacheOwnerHealth Health,
    long PhysicalBytes,
    long LogicalBytes,
    long OrphanBytes,
    IReadOnlyDictionary<string, long> Components,
    string? Detail = null)
{
    public static CacheStoreInspection Unavailable(string detail) =>
        new(
            CacheOwnerHealth.Unavailable,
            PhysicalBytes: 0,
            LogicalBytes: 0,
            OrphanBytes: 0,
            new Dictionary<string, long>(),
            detail);
}

public sealed record CacheOwnerCap(string Name, long Bytes);

public sealed record CacheClearFailure(string OwnerId, string ErrorType, string Message);

public sealed record CacheClearResidual(string Identity, string Reason);

public sealed class CacheClearPostconditionException : IOException
{
    public CacheClearPostconditionException(string ownerId, IReadOnlyList<CacheClearResidual> residuals)
        : base(
            $"The {ownerId} clear operation left {residuals.Count} residual item(s): " +
            string.Join(", ", residuals.Select(static residual =>
                $"{residual.Identity} ({residual.Reason})")))
    {
        OwnerId = ownerId;
        Residuals = residuals;
    }

    public string OwnerId { get; }

    public IReadOnlyList<CacheClearResidual> Residuals { get; }
}

public sealed class CacheClearException : Exception
{
    public CacheClearException(IReadOnlyList<CacheClearFailure> failures)
        : base(
            $"Failed to clear {failures.Count} cache owner(s): " +
            string.Join(", ", failures.Select(static failure =>
                $"{failure.OwnerId} ({failure.ErrorType}: {failure.Message})")))
    {
        Failures = failures;
    }

    public IReadOnlyList<CacheClearFailure> Failures { get; }
}
