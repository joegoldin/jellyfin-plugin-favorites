namespace Jellyfin.Plugin.Favorites;

public sealed class SyncState
{
    public Guid PlaylistId { get; set; }
    public HashSet<Guid> Baseline { get; set; } = [];
    public HashSet<Guid>? Pending { get; set; }
}

public sealed record Snapshot(HashSet<Guid> Hearts, HashSet<Guid>? Playlist);

public interface IFavoritesStore
{
    Task<SyncState?> Load(Guid userId, CancellationToken token);
    Task Save(Guid userId, SyncState state, CancellationToken token);
    Snapshot Read(Guid userId, Guid playlistId);
    Task<Guid> Create(Guid userId, IReadOnlyList<Guid> movies);
    Task Apply(Guid userId, Guid playlistId, HashSet<Guid> target, CancellationToken token);
}

public sealed class SyncEngine(IFavoritesStore store)
{
    // A single worker serializes all reads/writes. Persist intent before touching either side
    // so a crash or partial API failure is retried rather than mistaken for a user's edit.
    public async Task Sync(Guid userId, CancellationToken token)
    {
        var state = await store.Load(userId, token) ?? new SyncState();
        var snapshot = store.Read(userId, state.PlaylistId);
        if (snapshot.Playlist is null)
        {
            state.PlaylistId = await store.Create(userId, snapshot.Hearts.Order().ToArray());
            state.Baseline = snapshot.Hearts;
            state.Pending = null;
            await store.Save(userId, state, token);
            return;
        }

        if (state.Pending is null)
        {
            var target = Merge(state.Baseline, snapshot.Hearts, snapshot.Playlist);
            if (target.SetEquals(snapshot.Hearts) && target.SetEquals(snapshot.Playlist)
                && target.SetEquals(state.Baseline))
            {
                return;
            }

            state.Pending = target;
            await store.Save(userId, state, token);
        }

        await store.Apply(userId, state.PlaylistId, state.Pending, token);
        state.Baseline = state.Pending;
        state.Pending = null;
        await store.Save(userId, state, token);
    }

    public static HashSet<Guid> Merge(HashSet<Guid> baseline, HashSet<Guid> hearts, HashSet<Guid> playlist)
    {
        var result = new HashSet<Guid>();
        foreach (var id in baseline.Union(hearts).Union(playlist))
        {
            // Changed hearts take priority; otherwise a changed playlist supplies the intent.
            if (hearts.Contains(id) != baseline.Contains(id) ? hearts.Contains(id) : playlist.Contains(id))
            {
                result.Add(id);
            }
        }

        return result;
    }
}
