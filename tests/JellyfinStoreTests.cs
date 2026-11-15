using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Favorites;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Moq;
using Xunit;

namespace Favorites.Tests;

public sealed class JellyfinStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "favorites-tests-" + Guid.NewGuid());
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IPlaylistManager> _playlists = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly JellyfinStore _store;

    public JellyfinStoreTests()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_directory);
        _users.Setup(u => u.GetUserById(_userId)).Returns(new User("test", "auth", "reset") { Id = _userId });
        _store = new JellyfinStore(_library.Object, _users.Object, _userData.Object, _playlists.Object, paths.Object);
    }

    [Fact]
    public async Task RealAdapterCreatesPrivateVideoPlaylistWithAllInitialMovieIds()
    {
        var movies = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var id = Guid.NewGuid();
        PlaylistCreationRequest? actual = null;
        _playlists.Setup(p => p.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(r => actual = r)
            .ReturnsAsync(new PlaylistCreationResult(id.ToString("N")));
        Assert.Equal(id, await _store.Create(_userId, movies));
        Assert.NotNull(actual);
        Assert.Equal("Favorites", actual.Name);
        Assert.Equal(_userId, actual.UserId);
        Assert.False(actual.Public);
        Assert.Empty(actual.Users);
        Assert.Equal(MediaType.Video, actual.MediaType);
        Assert.Equal(movies, actual.ItemIdList);
    }

    [Fact]
    public void QueryImportsOnlyCurrentUsersFavoriteMovies()
    {
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
        var snapshot = _store.Read(_userId, Guid.Empty);
        Assert.Null(snapshot.Playlist);
        _library.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.User != null && q.User.Id == _userId && q.IsFavorite == true && q.Recursive == true
            && q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)), Times.Once);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RefusesPublicOrWrongOwnerPlaylist(bool isPublic, bool differentOwner)
    {
        var id = Guid.NewGuid();
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
        _library.Setup(l => l.GetItemById(id)).Returns(new Playlist
        {
            Id = id,
            OwnerUserId = differentOwner ? Guid.NewGuid() : _userId,
            OpenAccess = isPublic
        });
        Assert.Throws<InvalidOperationException>(() => _store.Read(_userId, id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FavoriteWritesPreservePlaybackDataAndUseCorrectOwner(bool favorite)
    {
        var id = Guid.NewGuid();
        var movie = new VisibleMovie { Id = Guid.NewGuid() };
        var data = new UserItemData { Key = movie.Id.ToString("N"), IsFavorite = !favorite, Played = true, PlaybackPositionTicks = 12345 };
        _library.Setup(l => l.GetItemById(id)).Returns(new Playlist { Id = id, OwnerUserId = _userId });
        _library.Setup(l => l.GetItemById(movie.Id)).Returns(movie);
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(favorite ? Array.Empty<BaseItem>() : new BaseItem[] { movie });
        _userData.Setup(u => u.GetUserData(It.IsAny<User>(), movie)).Returns(data);
        _playlists.Setup(p => p.AddItemToPlaylistAsync(id, It.IsAny<IReadOnlyCollection<Guid>>(), null, _userId))
            .Returns(Task.CompletedTask);
        await _store.Apply(_userId, id, favorite ? [movie.Id] : [], default);
        Assert.Equal(favorite, data.IsFavorite);
        Assert.True(data.Played);
        Assert.Equal(12345, data.PlaybackPositionTicks);
        _userData.Verify(u => u.SaveUserData(It.Is<User>(user => user.Id == _userId), movie, data,
            UserDataSaveReason.UpdateUserData, It.IsAny<CancellationToken>()), Times.Once);
        _playlists.Verify(p => p.AddItemToPlaylistAsync(id, It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(movie.Id)),
            null, _userId), favorite ? Times.Once() : Times.Never());
    }

    private sealed class VisibleMovie : Movie
    {
        public override bool IsVisible(User user, bool skipAllowedTagsCheck = false) => true;
    }

    [Fact]
    public async Task StateRoundTripsThroughRealFilesAndKeepsPendingJournal()
    {
        Assert.Null(await _store.Load(_userId, default));
        var id = Guid.NewGuid();
        var movie = Guid.NewGuid();
        await _store.Save(_userId, new SyncState { PlaylistId = id, Baseline = [movie], Pending = [] }, default);
        var state = await _store.Load(_userId, default);
        Assert.NotNull(state);
        Assert.Equal(id, state.PlaylistId);
        Assert.Contains(movie, state.Baseline);
        Assert.NotNull(state.Pending);
        Assert.Empty(state.Pending);
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "favorites-playlist")));
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task CorruptStateFailsClosedInsteadOfCreatingNewPlaylist(string json)
    {
        var directory = Path.Combine(_directory, "favorites-playlist");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, _userId.ToString("N") + ".json"), json);
        await Assert.ThrowsAnyAsync<Exception>(() => new SyncEngine(_store).Sync(_userId, default));
        _playlists.Verify(p => p.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
