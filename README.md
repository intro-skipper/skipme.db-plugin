# SkipMe.db Jellyfin Plugin

SkipMe.db is a Jellyfin media segment provider that downloads crowd-sourced
intro, credits, recap, preview, and commercial timestamps from the SkipMe.db API
and exposes them through Jellyfin's media segments API.

The plugin stores synced timestamps locally, lets you disable synced segments by
library, series, season, or movie, and can share locally saved Intro Skipper
timestamps back to SkipMe.db.

> [!CAUTION]
> Restrictions are in place due to bandwidth limitations, not as a means to hoard data.

## Requirements

- Jellyfin 12.0.0-rc2 or newer compatible 12 builds
- .NET 10 runtime support on the Jellyfin host
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
3. After a successful sync, the plugin queues Intro Skipper's segment detection
   when a compatible Intro Skipper is installed, or Jellyfin's media segment scan
   when running standalone.

By default, the sync task runs weekly on Sunday at 1:00 AM.

## Intro Skipper Integration

When Intro Skipper exposes the compatible SkipMe integration API, SkipMe.db
automatically supplies its local timestamps to Intro Skipper instead of registering
as a separate Jellyfin media segment provider. Intro Skipper treats available
SkipMe timestamps as authoritative and publishes the resulting segments. Saving
SkipMe's enable/disable settings also queues Intro Skipper detection.

The Sync and Share tabs and the SkipMe.db API remain available in both modes.
Series, season, movie, specials, and existing per-library SkipMe.db exclusions
remain respected by the integrated source. In integrated mode, enable Intro
Skipper as the library's Jellyfin segment provider; SkipMe.db no longer appears as
a separate provider. If Intro Skipper is absent or incompatible, SkipMe.db keeps
its standalone provider behavior. Restart Jellyfin after installing or removing
either plugin to reevaluate the integration.

Once Jellyfin finishes startup, integrated mode queues initial Intro Skipper
detection and removes legacy SkipMe-owned rows from Jellyfin's segment database.
Jellyfin already hides these rows when the standalone provider is no longer
registered. The local SkipMe database and other providers' segments are untouched.
Cleanup failures are logged and retried; Intro Skipper publishes replacement
segments asynchronously according to its own mirroring settings.

## Enabling, Disabling, and Priority

Jellyfin controls media segment providers per library. For standalone SkipMe.db:

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

- .NET SDK 10.x
- Node.js 22.x
- npm

Build the plugin:

```powershell
npm ci --prefix web
dotnet restore SkipMe.Db.Plugin.sln
dotnet build SkipMe.Db.Plugin.sln --configuration Release --no-restore
dotnet test SkipMe.Db.Plugin.sln --configuration Release --no-build
```

The web settings UI is built automatically during the .NET build and embedded in
the plugin assembly. The release DLL is written to:

```text
SkipMe.Db.Plugin/bin/Release/net10.0/SkipMe.Db.Plugin.dll
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
