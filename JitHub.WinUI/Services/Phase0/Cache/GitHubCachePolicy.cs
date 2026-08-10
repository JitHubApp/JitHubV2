using System;

namespace JitHub.Services;

public sealed class GitHubCachePolicy
{
    public const string SearchResource = "search";
    public const string MutableResource = "mutable";
    public const string RepositoryResource = "repo";
    public const string RepositoryMetadataResource = "repo-metadata";
    public const string LookupResource = "lookup";
    public const string ImmutableShaResource = "immutable-sha";
    public const string AvatarImageResource = "avatar-image";

    public static readonly GitHubCachePolicy Default = new();

    public GitHubCachePolicy(
        long metadataSoftCapBytes = 128L * 1024L * 1024L,
        long payloadSoftCapBytes = 2L * 1024L * 1024L * 1024L,
        long avatarImageSoftCapBytes = 256L * 1024L * 1024L)
    {
        MetadataSoftCapBytes = metadataSoftCapBytes;
        PayloadSoftCapBytes = payloadSoftCapBytes;
        AvatarImageSoftCapBytes = avatarImageSoftCapBytes;
    }

    public long MetadataSoftCapBytes { get; }

    public long PayloadSoftCapBytes { get; }

    public long AvatarImageSoftCapBytes { get; }

    public string DescribeQueryTtlPolicy() => string.Join(
        "; ",
        $"{FormatDuration(TtlForResource(MutableResource))} mutable",
        $"{FormatDuration(TtlForResource(SearchResource))} search",
        $"{FormatDuration(TtlForResource(RepositoryResource))} repository metadata",
        $"{FormatDuration(TtlForResource(LookupResource))} lookups",
        $"{FormatDuration(TtlForResource(ImmutableShaResource))} immutable SHA content");

    public static TimeSpan TtlForResource(string resourceKind) =>
        resourceKind switch
        {
            SearchResource => TimeSpan.FromMinutes(15),
            RepositoryResource => TimeSpan.FromMinutes(30),
            RepositoryMetadataResource => TimeSpan.FromHours(1),
            LookupResource => TimeSpan.FromHours(1),
            ImmutableShaResource => TimeSpan.FromDays(30),
            AvatarImageResource => TimeSpan.FromDays(7),
            _ => TimeSpan.FromMinutes(5)
        };

    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalDays >= 1
            ? $"{duration.TotalDays:0.#} days"
            : duration.TotalHours >= 1
                ? $"{duration.TotalHours:0.#} hours"
                : $"{duration.TotalMinutes:0.#} minutes";
}
