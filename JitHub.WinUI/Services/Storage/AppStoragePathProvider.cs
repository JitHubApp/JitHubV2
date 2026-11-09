using System;
using System.IO;
using Windows.Storage;

namespace JitHub.Services;

public interface IAppStoragePathProvider
{
    string CacheDatabasePath { get; }

    string PayloadRootPath { get; }

    string ImageRootPath { get; }

    string DiagnosticsPath { get; }

    string StarLibraryDatabasePath { get; }

    string StarLibraryRecoveryPath { get; }

    string GistMutationJournalPath { get; }

    string AccountRemovalJournalRootPath { get; }
}

public sealed class AppStoragePathProvider : IAppStoragePathProvider
{
    private const string CacheRootName = "GitHubCache";
    private const string CacheVersionName = "v1";
    private const string DiagnosticsRootName = "Diagnostics";
    private const string DiagnosticsVersionName = "v1";
    private const string StarsRootName = "Stars";
    private const string StarsVersionName = "v1";
    private const string GistsRootName = "Gists";
    private const string GistsVersionName = "v1";
    private const string AccountRemovalRootName = "AccountRemoval";
    private const string AccountRemovalVersionName = "v1";

    public AppStoragePathProvider()
        : this(GetLocalCachePath(), GetLocalFolderPath())
    {
    }

    internal AppStoragePathProvider(string localCachePath, string localFolderPath)
    {
        string cacheRoot = Path.Combine(localCachePath, CacheRootName, CacheVersionName);
        string diagnosticsRoot = Path.Combine(localFolderPath, DiagnosticsRootName, DiagnosticsVersionName);
        string starsRoot = Path.Combine(localFolderPath, StarsRootName, StarsVersionName);
        string gistsRoot = Path.Combine(localFolderPath, GistsRootName, GistsVersionName);
        string accountRemovalRoot = Path.Combine(localFolderPath, AccountRemovalRootName, AccountRemovalVersionName);

        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(diagnosticsRoot);
        Directory.CreateDirectory(starsRoot);
        Directory.CreateDirectory(gistsRoot);
        Directory.CreateDirectory(accountRemovalRoot);

        CacheDatabasePath = Path.Combine(cacheRoot, "jithub-cache.db");
        PayloadRootPath = Path.Combine(cacheRoot, "payloads");
        ImageRootPath = Path.Combine(cacheRoot, "images");
        DiagnosticsPath = Path.Combine(diagnosticsRoot, "diagnostics.ndjson");
        StarLibraryDatabasePath = Path.Combine(starsRoot, "jithub-stars.db");
        StarLibraryRecoveryPath = Path.Combine(starsRoot, "repository-action-recovery.json");
        GistMutationJournalPath = Path.Combine(gistsRoot, "mutation-journal.json");
        AccountRemovalJournalRootPath = accountRemovalRoot;

        Directory.CreateDirectory(PayloadRootPath);
        Directory.CreateDirectory(ImageRootPath);
    }

    public string CacheDatabasePath { get; }

    public string PayloadRootPath { get; }

    public string ImageRootPath { get; }

    public string DiagnosticsPath { get; }

    public string StarLibraryDatabasePath { get; }

    public string StarLibraryRecoveryPath { get; }

    public string GistMutationJournalPath { get; }

    public string AccountRemovalJournalRootPath { get; }

    private static string GetLocalCachePath()
    {
        if (AppDataPathPolicy.TryGetAutomationRoots(out _, out string localCachePath))
        {
            return localCachePath;
        }

        try
        {
            return ApplicationData.Current.LocalCacheFolder.Path;
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JitHub",
                "LocalCache");
        }
    }

    private static string GetLocalFolderPath()
    {
        if (AppDataPathPolicy.TryGetAutomationRoots(out string localFolderPath, out _))
        {
            return localFolderPath;
        }

        try
        {
            return ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JitHub");
        }
    }
}
