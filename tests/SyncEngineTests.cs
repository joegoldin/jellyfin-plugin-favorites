using System.Text.Json;
using Jellyfin.Plugin.Favorites;
using Xunit;

namespace Favorites.Tests;

public sealed class SyncEngineTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public async Task FirstCreationImportsEveryExistingFavorite()
    {
        var store = new MemoryStore();
        store.For(User).Hearts.UnionWith([A, B]);
        await new SyncEngine(store).Sync(User, default);
        Assert.Equal("Favorites", store.CreatedName);
        Assert.True(store.For(User).Playlist!.SetEquals([A, B]));
        Assert.True(store.State(User)!.Baseline.SetEquals([A, B]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdditionsSyncInBothDirections(bool fromHearts)
    {
        var store = await Initialized();
        (fromHearts ? store.For(User).Hearts : store.For(User).Playlist!).Add(B);
        await new SyncEngine(store).Sync(User, default);
        Assert.True(store.For(User).Hearts.SetEquals([A, B]));
        Assert.True(store.For(User).Playlist!.SetEquals([A, B]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovalsSyncBothWaysAndStayRemoved(bool fromHearts)
    {
        var store = await Initialized();
        (fromHearts ? store.For(User).Hearts : store.For(User).Playlist!).Remove(A);
        await new SyncEngine(store).Sync(User, default);
        await new SyncEngine(store).Sync(User, default);
        Assert.Empty(store.For(User).Hearts);
        Assert.Empty(store.For(User).Playlist!);
    }

    [Fact]
    public async Task RestartRetainsBaselineAndAppliesOfflinePlaylistRemoval()
    {
        var store = await Initialized();
        store.For(User).Playlist!.Remove(A);
        await new SyncEngine(store).Sync(User, default);
        Assert.Empty(store.For(User).Hearts);
    }

    [Fact]
    public async Task DeletedPlaylistIsRecreatedWithoutUnfavoritingMovies()
    {
        var store = await Initialized();
        store.For(User).Playlist = null;
        await new SyncEngine(store).Sync(User, default);
        Assert.True(store.For(User).Playlist!.SetEquals([A]));
        Assert.True(store.For(User).Hearts.SetEquals([A]));
    }

    [Fact]
    public async Task PartialFailureRetriesPersistedIntentAfterRestart()
    {
        var store = await Initialized();
        store.For(User).Playlist!.Remove(A);
        store.FailAfterHearts = true;
        await Assert.ThrowsAsync<IOException>(() => new SyncEngine(store).Sync(User, default));
        Assert.NotNull(store.State(User)!.Pending);
        Assert.Contains(A, store.State(User)!.Baseline);
        store.FailAfterHearts = false;
        await new SyncEngine(store).Sync(User, default);
        Assert.Empty(store.For(User).Hearts);
        Assert.Empty(store.For(User).Playlist!);
        Assert.Null(store.State(User)!.Pending);
    }

    [Fact]
    public async Task UserStateIsIsolated()
    {
        var store = await Initialized();
        var other = Guid.NewGuid();
        store.For(other).Hearts.Add(B);
        await new SyncEngine(store).Sync(other, default);
        store.For(User).Playlist!.Clear();
        await new SyncEngine(store).Sync(User, default);
        Assert.True(store.For(other).Hearts.SetEquals([B]));
        Assert.True(store.For(other).Playlist!.SetEquals([B]));
    }

    [Fact]
    public void IndependentEditsOnOppositeSidesAreBothPreserved()
    {
        Assert.True(SyncEngine.Merge([A], [A, B], []).SetEquals([B]));
    }

    [Fact]
    public async Task UnchangedStateDoesNotWriteAndReorderingDoesNotChangeFavorites()
    {
        var store = await Initialized();
        var saves = store.Saves;
        await new SyncEngine(store).Sync(User, default);
        Assert.Equal(saves, store.Saves);
        Assert.Equal(0, store.Applies);
    }

    [Fact]
    public async Task SaveFailureStopsBeforeModifyingEitherSide()
    {
        var store = await Initialized();
        store.For(User).Playlist!.Clear();
        store.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => new SyncEngine(store).Sync(User, default));
        Assert.Contains(A, store.For(User).Hearts);
        Assert.Equal(0, store.Applies);
    }

    private static async Task<MemoryStore> Initialized()
    {
        var store = new MemoryStore();
        store.For(User).Hearts.Add(A);
        await new SyncEngine(store).Sync(User, default);
        return store;
    }

    private sealed class UserData
    {
        public HashSet<Guid> Hearts { get; set; } = [];
        public HashSet<Guid>? Playlist { get; set; }
    }

    private sealed class MemoryStore : IFavoritesStore
    {
        private readonly Dictionary<Guid, UserData> _users = [];
        private readonly Dictionary<Guid, string> _states = [];
        public bool FailAfterHearts { get; set; }
        public bool FailSave { get; set; }
        public int Saves { get; private set; }
        public int Applies { get; private set; }
        public string? CreatedName { get; private set; }
        public UserData For(Guid user) => _users.TryGetValue(user, out var data)
            ? data : _users[user] = new UserData();
        public SyncState? State(Guid user) => _states.TryGetValue(user, out var json)
            ? JsonSerializer.Deserialize<SyncState>(json) : null;
        public Task<SyncState?> Load(Guid userId, CancellationToken token) => Task.FromResult(State(userId));
        public Task Save(Guid userId, SyncState state, CancellationToken token)
        {
            if (FailSave) throw new IOException("Disk unavailable");
            _states[userId] = JsonSerializer.Serialize(state);
            Saves++;
            return Task.CompletedTask;
        }
        public Snapshot Read(Guid userId, Guid playlistId) =>
            new(new HashSet<Guid>(For(userId).Hearts), For(userId).Playlist is { } p ? new HashSet<Guid>(p) : null);
        public Task<Guid> Create(Guid userId, IReadOnlyList<Guid> movies)
        {
            CreatedName = "Favorites";
            For(userId).Playlist = movies.ToHashSet();
            return Task.FromResult(Guid.NewGuid());
        }
        public Task Apply(Guid userId, Guid playlistId, HashSet<Guid> target, CancellationToken token)
        {
            Applies++;
            For(userId).Hearts = new HashSet<Guid>(target);
            if (FailAfterHearts) throw new IOException("Playlist unavailable");
            For(userId).Playlist = new HashSet<Guid>(target);
            return Task.CompletedTask;
        }
    }
}
