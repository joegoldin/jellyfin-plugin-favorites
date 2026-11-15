# Favorites Playlist for Jellyfin

Keeps each user's Jellyfin movie hearts and a private **Favorites** playlist in sync.
Designed for Infuse on Apple TV, where you can add movies to a Jellyfin playlist
instead of using Jellyfin's heart button.

## What it does

- Creates a private Favorites playlist for every Jellyfin user, including new users.
- **Imports all movies that user has already favorited when creating the playlist.**
- Heart a movie in Jellyfin → it appears in that user's playlist.
- Unheart a movie → it leaves the playlist.
- Add a movie to the playlist in Infuse or Jellyfin → it gets a Jellyfin heart.
- Remove a movie from the playlist → its Jellyfin heart is cleared.
- Checks approximately every five seconds, starting 15 seconds after server startup.
  Infuse's own refresh/cache can add delay.
- Preserves sync state across server restarts. Deleting the entire playlist creates
  a replacement from current hearts; it does **not** clear all hearts.

**Movies only.** TV series, seasons, episodes, music and other item types are not
synchronized. Non-movie entries you add to the playlist are left alone. Infuse must
be editing the server-side Jellyfin playlist; its local favorites are separate.

## Install

Requires **Jellyfin 12.4.0** (.NET 10). Other Jellyfin versions have not been tested.

1. In Jellyfin, open **Dashboard → Plugins → Repositories → Add**.
2. Name it Favorites Playlist and use this repository URL:

   `https://raw.githubusercontent.com/joegoldin/jellyfin-plugin-favorites/main/manifest.json`

3. Save, open the plugin catalog, install **Favorites Playlist**, and restart Jellyfin.
4. Wait about 20 seconds. Each user should see their own Favorites playlist.
   Refresh Infuse's library if necessary.

No API key, password, scheduled script or configuration is needed.

## Safety and sync details

The plugin never takes over a pre-existing playlist by name. If you already have
one named Favorites, both will exist; only the newly created playlist is managed.
Renaming the managed playlist is safe because it is tracked by ID.

Keep the managed playlist **private and unshared**. Sync stops for a user if that
playlist is shared or its owner changes, and logs an error rather than mixing
users' favorites. Jellyfin server administrators retain their normal administrative
access.

Each reconciliation compares current hearts and playlist membership against the
last successfully synced set. Independent changes on either side are preserved.
A changed heart takes precedence if the same item has conflicting intent.
Reordering the playlist does not affect hearts. Existing ordering is retained;
new entries append in stable ID order.

Intent is journaled before writes and replayed after a failure. During recovery,
the pending sync intent takes precedence over later edits to the same items;
make those edits again after recovery. As with any polling sync, avoid editing
the same movie simultaneously from two clients.

State lives under `<Jellyfin data directory>/favorites-playlist/<user-id>.json`.
Back up this folder with Jellyfin's normal data. Corrupt/unreadable state stops
that user's sync and is logged; it is not silently overwritten. Deleting state
makes the plugin create a new playlist and leaves the old one alone. A crash
between playlist creation and its first state save can also leave an unmanaged
duplicate playlist; existing hearts remain intact.

Removing access to a movie or removing it from the library drops it out of the
managed set; the plugin never updates hearts on items the user cannot access.

## Uninstall

Uninstall from Jellyfin's plugin dashboard and restart. Playlists and heart
favorites are left as they were. Remove unwanted playlists manually afterward.

## Build and test

With the .NET 10 SDK and Python 3 installed:

```fish
dotnet test tests/Favorites.Tests.csproj --configuration Release
dotnet build Jellyfin.Plugin.Favorites/Jellyfin.Plugin.Favorites.csproj --configuration Release
python3 scripts/package.py --timestamp 2026-11-15T00:00:00Z
```

The ZIP contains only the plugin DLL; Jellyfin supplies its own assemblies.
The packaging script generates `artifacts/manifest.json` with the ZIP's MD5
checksum, as required by Jellyfin's repository format.

CI runs on Linux. Automated tests cover the sync engine, Jellyfin adapter contract
and state serialization. Live Jellyfin/Infuse behavior needs an installation smoke
test; publishing the plugin does not install it on your server.
