# SkipMe.db Jellyfin Plugin

SkipMe.db is a Jellyfin media segment provider that downloads crowd-sourced
intro, credits, recap, preview, and commercial timestamps from the SkipMe.db API
and exposes them through Jellyfin's media segments API.

The plugin stores synced timestamps locally, lets you disable synced segments by
library, series, season, or movie, and can share locally saved Intro Skipper
timestamps back to SkipMe.db.

## Requirements

- Jellyfin 10.11.5 or newer compatible 10.11 builds
- .NET 9 runtime support on the Jellyfin host
- Network access from Jellyfin to:
  - `https://db.skipme.workers.dev`
  - `https://api.tvmaze.com` when sharing show timestamps that need missing
    external IDs resolved

## Installation

1. Download the latest `SkipMe.db-plugin-*.zip` from the project releases.
2. Extract `SkipMe.Db.Plugin.dll`.
3. Copy the DLL into Jellyfin's plugin directory, for example:
   - Linux: `/var/lib/jellyfin/plugins/SkipMe.db/`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\SkipMe.db\`
4. Restart Jellyfin.
5. Confirm that `SkipMe.db` appears under Dashboard -> Plugins.

## First Sync

The plugin adds a scheduled task named `Sync SkipMe.db Segment Database` in the
`Intro Skipper` task category.

To populate the local segment database immediately:

1. Go to Dashboard -> Scheduled Tasks.
2. Run `Sync SkipMe.db Segment Database`.
3. After a successful sync, the plugin queues Jellyfin's media segment scan so
   Jellyfin can pick up the new timestamps.

By default, the sync task runs weekly on Sunday at 1:00 AM.

## Enabling, Disabling, and Priority

Jellyfin controls media segment providers per library.

1. Navigate to Dashboard -> Libraries -> Libraries.
2. Open the desired library menu (`...`) -> Manage library.
3. Scroll to `Media segment providers`.
4. Enable `SkipMe.db` and adjust provider priority as needed.

Inside the plugin settings page, the `Sync` tab lets you suppress synced
SkipMe.db data for individual series, seasons, or movies. Disabled items remain
in the local database, but the plugin does not surface them to Jellyfin.

Specials seasons, season 0, are disabled by default. Enable a specials season
explicitly in the plugin settings if you want those timestamps to appear.

## Plugin Settings

Open Dashboard -> Plugins -> SkipMe.db.

- `Sync` tab: choose which synced SkipMe.db segments Jellyfin can use.
- `Share` tab: choose which local Intro Skipper timestamps to upload to
  SkipMe.db.
- Filter box: search large libraries before changing toggles or sharing.
- `Save Settings`: persists the current Sync tab enable/disable choices.
- `Share Enabled Items`: submits the currently enabled Share tab items.

Library-level provider disabling in Jellyfin is respected by the settings page:
libraries where `SkipMe.db` is disabled as a media segment provider are hidden
from the plugin item list.

## Sharing Segments to SkipMe.db

The Share tab reads timestamps from Intro Skipper's local database at
`introskipper/introskipper.db` under Jellyfin's data directory.

Sharing behavior:

- Only items enabled in the Share tab are submitted.
- Existing local share history is used to avoid re-submitting the same timestamp
  within a one second tolerance.
- Segment editor entries are preferred over auto-detected timestamps when both
  exist for the same item and segment type.
- Movies require duration plus at least one supported provider ID: TMDb, IMDb,
  TVDB, or AniList.
- Shows use season, episode, duration, and available provider IDs. If series IDs
  are missing, the plugin may query TVMaze to fill in TVDB or IMDb IDs.

After a share finishes, the settings page reports how many segments were shared
and how many were skipped because they were already shared, missing metadata, or
had no local Intro Skipper timestamps.

## Building from Source

Prerequisites:

- .NET SDK 9.x
- Node.js 22.x
- npm

Build the plugin:

```powershell
npm ci --prefix web
dotnet restore SkipMe.Db.Plugin.sln
dotnet build SkipMe.Db.Plugin.sln --configuration Release --no-restore
```

The web settings UI is built automatically during the .NET build and embedded in
the plugin assembly. The release DLL is written to:

```text
SkipMe.Db.Plugin/bin/Release/net9.0/SkipMe.Db.Plugin.dll
```

For front-end-only development:

```powershell
cd web
npm ci
npm run dev
```

## License

This project is licensed under the GPL-3.0-only license. See `LICENSE` and
`NOTICE` for details.
