#!/usr/bin/env python3
"""Package only the plugin assembly; Jellyfin supplies its own dependencies."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = "1.0.0.0"
TAG = "v" + VERSION
NAME = "jellyfin-plugin-favorites_" + VERSION + ".zip"
DLL = ROOT / "Jellyfin.Plugin.Favorites/bin/Release/net10.0/Jellyfin.Plugin.Favorites.dll"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--timestamp", required=True, help="ISO-8601 UTC release timestamp")
    args = parser.parse_args()
    output = ROOT / "artifacts"
    output.mkdir(exist_ok=True)
    archive = output / NAME
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
        info = zipfile.ZipInfo(DLL.name, date_time=(2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        package.writestr(info, DLL.read_bytes())
    manifest = [{
        "guid": "fbd0367a-0af9-4fbd-831c-3ebcb82b9918",
        "name": "Favorites Playlist",
        "description": "Bidirectional sync between each user's movie hearts and a private Favorites playlist. Imports existing favorites on first creation.",
        "overview": "Sync movie hearts with a private Favorites playlist for Infuse.",
        "owner": "joegoldin",
        "category": "General",
        "versions": [{
            "version": VERSION,
            "changelog": "Initial release: existing movie favorite import, two-way sync, per-user private playlists and restart-safe reconciliation.",
            "targetAbi": "12.4.0.0",
            "sourceUrl": f"https://github.com/joegoldin/jellyfin-plugin-favorites/releases/download/{TAG}/{NAME}",
            "checksum": hashlib.md5(archive.read_bytes()).hexdigest(),
            "timestamp": args.timestamp
        }]
    }]
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(archive)


if __name__ == "__main__":
    main()
