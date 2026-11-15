using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;

namespace Jellyfin.Plugin.Favorites;

public sealed class JellyfinStore(
    ILibraryManager library,
    IUserManager users,
    IUserDataManager userData,
    IPlaylistManager playlists,
    IApplicationPaths paths) : IFavoritesStore
{
    private readonly string _directory = Path.Combine(paths.DataPath, "favorites-playlist");

    private string StatePath(Guid userId) => Path.Combine(_directory, userId.ToString("N") + ".json");

    public async Task<SyncState?> Load(Guid userId, CancellationToken token)
    {
        var path = StatePath(userId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<SyncState>(stream, cancellationToken: token);
        if (state is null || state.PlaylistId == Guid.Empty || state.Baseline is null
            || state.Baseline.Contains(Guid.Empty) || state.Pending?.Contains(Guid.Empty) == true)
        {
            throw new InvalidDataException("Favorites state is invalid. Restore its backup before syncing.");
        }

        return state;
    }

    public async Task Save(Guid userId, SyncState state, CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        var path = StatePath(userId);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: token);
            await stream.FlushAsync(token);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private Jellyfin.Database.Implementations.Entities.User User(Guid id) =>
        users.GetUserById(id) ?? throw new InvalidOperationException("User no longer exists.");

    private Playlist? OwnedPlaylist(Guid userId, Guid id)
    {
        if (id == Guid.Empty || library.GetItemById(id) is not Playlist playlist)
        {
            return null;
        }

        // Never let sharing or an ownership change leak favorites between users.
        if (playlist.OwnerUserId != userId || playlist.OpenAccess || playlist.Shares.Count != 0)
        {
            throw new InvalidOperationException("Favorites playlist must remain private and owned by its original user.");
        }

        return playlist;
    }

    public Snapshot Read(Guid userId, Guid playlistId)
    {
        var user = User(userId);
        var hearts = library.GetItemList(new InternalItemsQuery(user)
        {
            Recursive = true,
            IsFavorite = true,
            IncludeItemTypes = [BaseItemKind.Movie]
        }).Select(item => item.Id).ToHashSet();
        var playlist = OwnedPlaylist(userId, playlistId);
        return new Snapshot(hearts, playlist?.GetManageableItems()
            .Where(entry => entry.Item2 is Movie && entry.Item2.IsVisible(user))
            .Select(entry => entry.Item2.Id).ToHashSet());
    }

    public async Task<Guid> Create(Guid userId, IReadOnlyList<Guid> movies)
    {
        var result = await playlists.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = "Favorites",
            UserId = userId,
            Public = false,
            MediaType = MediaType.Video,
            ItemIdList = movies
        });
        return Guid.Parse(result.Id);
    }

    public async Task Apply(Guid userId, Guid playlistId, HashSet<Guid> target, CancellationToken token)
    {
        var user = User(userId);
        var playlist = OwnedPlaylist(userId, playlistId)
            ?? throw new InvalidOperationException("Favorites playlist was deleted during sync.");
        var snapshot = Read(userId, playlistId);
        foreach (var id in snapshot.Hearts.Union(target))
        {
            token.ThrowIfCancellationRequested();
            if (library.GetItemById(id) is not Movie movie || !movie.IsVisible(user))
            {
                continue;
            }

            var data = userData.GetUserData(user, movie)
                ?? throw new InvalidOperationException("Movie user data could not be loaded.");
            if (data.IsFavorite != target.Contains(id))
            {
                data.IsFavorite = target.Contains(id);
                userData.SaveUserData(user, movie, data, UserDataSaveReason.UpdateUserData, token);
            }
        }

        var entries = playlist.GetManageableItems();
        var removals = entries.Where(entry => entry.Item2 is Movie && entry.Item2.IsVisible(user)
                && !target.Contains(entry.Item2.Id))
            .Select(entry => entry.Item1.ItemId?.ToString("N")
                ?? throw new InvalidDataException("Playlist entry has no ID."))
            .ToArray();
        if (removals.Length > 0)
        {
            await playlists.RemoveItemFromPlaylistAsync(playlistId.ToString("N"), removals);
        }

        var existing = entries.Select(entry => entry.Item2.Id).ToHashSet();
        var additions = target.Except(existing)
            .Where(id => library.GetItemById(id) is Movie movie && movie.IsVisible(user))
            .Order().ToArray();
        if (additions.Length > 0)
        {
            await playlists.AddItemToPlaylistAsync(playlistId, additions, null, userId);
        }
    }
}
