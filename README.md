# SkipMe.db Jellyfin Plugin

SkipMe.db is a Jellyfin media segment provider for crowd-sourced intro, recap,
preview, credits, and commercial timestamps. It synchronizes the data into a
local SQLite cache and makes applicable segments available to Jellyfin for
movies and TV episodes.

The plugin also provides an optional sharing workflow for contributing local
Intro Skipper timestamps back to SkipMe.db.

<div align="center">
  <br/>
  <p align="center">
    <a href="https://discord.gg/QB9U47BpX6"><img src="https://invidget.switchblade.xyz/AYZ7RJ3BuA"></a>
  </p>
</div>

## Requirements

- Jellyfin 12.0.0-rc2 or a compatible Jellyfin 12 build
- .NET 10 runtime support on the Jellyfin host
- Network access from Jellyfin to the SkipMe.db service
- Optional network access to TVMaze when sharing shows whose external IDs are
  missing

## Installation

1. Download the latest `SkipMe.db-plugin-*.zip` from the project releases.
2. Extract `SkipMe.Db.Plugin.dll`.
3. Copy the DLL into Jellyfin's plugin directory, for example:
   - Linux: `/var/lib/jellyfin/plugins/SkipMe.db/`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\SkipMe.db\`
4. Restart Jellyfin.
5. Confirm that `SkipMe.db` appears under Dashboard → Plugins.

## How synchronization works

The plugin scans non-virtual movies and TV episodes in the Jellyfin library,
uses available TMDB, TVDB, IMDb, and AniList identifiers to match them, and
stores the resulting timestamps locally. TV episodes are grouped into series
lookups where possible; episodes that cannot use a series lookup can fall back
to an individual media lookup. Later synchronizations reuse timestamps already
stored locally and query only items that have not been retrieved yet; items for
which the service returned no timestamps remain eligible for a later retry.

Synchronization is provided by the visible `Sync SkipMe.db Segment Database`
task in the `Intro Skipper` category. It runs once automatically after the
plugin is first loaded, then daily at 01:00 local time. Manual or scheduled
starts are limited to one attempt every four hours. The task can report
progress, avoids overlapping runs, and keeps the existing cache if a run is
cancelled, incomplete, or reaches the remote service's daily usage limit.

Older cache locations are migrated when possible. The plugin uses the local
cache when serving segments, so a temporary service failure does not remove
previously synchronized data.

## Enabling the provider

Jellyfin controls media segment providers per library:

1. Open Dashboard → Libraries → Libraries.
2. Open the desired library menu (`...`) and choose Manage library.
3. Find Media segment providers.
4. Enable `SkipMe.db` and adjust provider priority as needed.

Only movies and TV episodes are supported. The provider maps recognized
SkipMe.db segment types to Jellyfin media segment types and ignores invalid or
unknown timestamp data.

## Plugin settings

Open Dashboard → Plugins → SkipMe.db.

### Skip

The Skip tab controls which synchronized segments Jellyfin may use:

- Toggle an entire library, series, season, or movie.
- Expand a series to manage individual seasons.
- Season 0 (Specials) is disabled by default and must be explicitly enabled.
- Disabled items remain in the local cache but are not surfaced to Jellyfin.
- Save the changes with `Save Settings`.

Libraries where `SkipMe.db` is disabled as a Jellyfin media segment provider
are not shown in the settings page. The filter box searches the displayed
library items, and the page shows a notice when a very large library cannot be
loaded in one result set.

### Share

The Share tab reads local timestamps from Intro Skipper's SQLite database.

Sharing is opt-in: items start disabled on each page load, and only items
enabled in the Share tab are submitted. The page displays counts for
timestamps available to share and reports the result after each run.

Sharing behavior includes:

- Movies and TV episodes are supported.
- Valid duration and at least one supported external identifier are required.
- Supported identifiers include TMDB, TVDB, IMDb, and AniList, with episode,
  season, and series metadata used as appropriate.
- If a show has no usable series identifiers, the plugin may query TVMaze to
  resolve TVDB or IMDb identifiers. Results are cached per Jellyfin series.
- Intro Skipper segment-editor entries take priority over auto-detected entries
  of the same type; when priorities are equal, the earliest valid entry is
  selected.
- One canonical timestamp per segment type is considered for each item.
- Local share history prevents re-submitting matching timestamps within one
  second for the item, segment type, start, end, and duration.

The completion message reports shared timestamps and items skipped because
they were already shared, lacked metadata, or had no valid local timestamp.

## License

This project is licensed under the GPL-3.0-only license. See `LICENSE` and
`NOTICE` for details.
