using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Favorites;

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer)
    : BasePlugin<MediaBrowser.Model.Plugins.BasePluginConfiguration>(paths, serializer)
{
    public override string Name => "Favorites Playlist";
    public override string Description => "Keeps each user's movie favorites and private Favorites playlist in sync.";
    public override Guid Id => Guid.Parse("fbd0367a-0af9-4fbd-831c-3ebcb82b9918");
}
