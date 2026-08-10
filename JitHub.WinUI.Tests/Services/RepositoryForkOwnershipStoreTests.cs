using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepositoryForkOwnershipStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "JitHubForkOwnershipTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AcceptedOwnershipSurvivesStoreRecreationAndCanBeRemoved()
    {
        string path = Path.Combine(_root, "forks.json");
        string key = RepositoryForkOwnershipKey.Create("42", 7, "source", "repo", "viewer");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RepositoryForkOwnershipState state = new(
            key,
            "42",
            7,
            "source",
            "repo",
            "viewer",
            "repo",
            RepositoryForkOwnershipStatus.Accepted,
            19,
            2,
            now,
            now);

        RepositoryForkOwnershipStore writer = new(path);
        await writer.UpsertAsync(state);

        RepositoryForkOwnershipStore reader = new(path);
        RepositoryForkOwnershipState? restored = await reader.GetAsync(key);
        Assert.Equal(state, restored);

        await reader.RemoveAsync(key);
        Assert.Null(await new RepositoryForkOwnershipStore(path).GetAsync(key));
    }

    [Fact]
    public async Task UncertainOwnershipSurvivesStoreRecreation()
    {
        string path = Path.Combine(_root, "uncertain-forks.json");
        string key = RepositoryForkOwnershipKey.Create("42", 7, "source", "repo", "viewer");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RepositoryForkOwnershipState uncertain = new(
            key, "42", 7, "source", "repo", "viewer", "repo",
            RepositoryForkOwnershipStatus.Uncertain, null, 1, now, now);

        await new RepositoryForkOwnershipStore(path).UpsertAsync(uncertain);

        RepositoryForkOwnershipState? restored = await new RepositoryForkOwnershipStore(path).GetAsync(key);
        Assert.Equal(uncertain, restored);
    }

    [Fact]
    public async Task MalformedOwnershipIsQuarantinedAndBlocksDuplicateCreationAcrossRestart()
    {
        string path = Path.Combine(_root, "malformed-forks.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(path, "{ definitely-not-valid-json");
        string key = RepositoryForkOwnershipKey.Create("42", 7, "source", "repo", "viewer");

        RepositoryForkOwnershipState? first = await new RepositoryForkOwnershipStore(path).GetAsync(key);
        RepositoryForkOwnershipState? afterRestart = await new RepositoryForkOwnershipStore(path).GetAsync(key);

        Assert.Equal(RepositoryForkOwnershipStatus.Accepted, first?.Status);
        Assert.Equal(RepositoryForkOwnershipStatus.Accepted, afterRestart?.Status);
        Assert.True(File.Exists(path + ".quarantine-guard"));
        Assert.Contains(
            Directory.GetFiles(_root, "malformed-forks.json.quarantine-*"),
            file => !file.EndsWith(".quarantine-guard", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VersionInvalidOwnershipPreservesKnownUncertainStateAndQuarantinesUnknownKeys()
    {
        string path = Path.Combine(_root, "old-version-forks.json");
        Directory.CreateDirectory(_root);
        string knownKey = RepositoryForkOwnershipKey.Create("42", 7, "source", "repo", "viewer");
        string unknownKey = RepositoryForkOwnershipKey.Create("42", 8, "source", "other", "viewer");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RepositoryForkOwnershipState known = new(
            knownKey, "42", 7, "source", "repo", "viewer", "repo",
            RepositoryForkOwnershipStatus.Uncertain, null, 2, now, now);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Version = 0,
            Items = new[] { known }
        }));

        RepositoryForkOwnershipStore store = new(path);
        RepositoryForkOwnershipState? restoredKnown = await store.GetAsync(knownKey);
        RepositoryForkOwnershipState? conservativeUnknown = await new RepositoryForkOwnershipStore(path).GetAsync(unknownKey);

        Assert.Equal(known, restoredKnown);
        Assert.Equal(RepositoryForkOwnershipStatus.Accepted, conservativeUnknown?.Status);
        Assert.True(File.Exists(path + ".quarantine-guard"));
    }

    [Fact]
    public async Task UpsertIsPartitionedByAccountSourceAndTarget()
    {
        string path = Path.Combine(_root, "forks.json");
        RepositoryForkOwnershipStore store = new(path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string firstKey = RepositoryForkOwnershipKey.Create("1", 7, "source", "repo", "viewer");
        string secondKey = RepositoryForkOwnershipKey.Create("2", 7, "source", "repo", "viewer");

        await store.UpsertAsync(new RepositoryForkOwnershipState(
            firstKey, "1", 7, "source", "repo", "viewer", "repo",
            RepositoryForkOwnershipStatus.Uncertain, null, 0, now, now));
        await store.UpsertAsync(new RepositoryForkOwnershipState(
            secondKey, "2", 7, "source", "repo", "viewer", "repo",
            RepositoryForkOwnershipStatus.Accepted, 21, 1, now, now));

        Assert.Equal(RepositoryForkOwnershipStatus.Uncertain, (await store.GetAsync(firstKey))?.Status);
        Assert.Equal(RepositoryForkOwnershipStatus.Accepted, (await store.GetAsync(secondKey))?.Status);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
