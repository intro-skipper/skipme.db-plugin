# SkipMe.db Jellyfin Plugin

SkipMe.db is a Jellyfin 12 media-segment provider. It downloads crowd-sourced
intro, recap, credits, and preview timestamps from SkipMe.db, stores them in a
local SQLite cache, and exposes them to Jellyfin for movies and TV episodes.

The plugin also provides an opt-in Share workflow that submits eligible local
timestamps from Intro Skipper back to SkipMe.db.

<div align="center">
  <br/>
  <p align="center">
    <a href="https://discord.gg/QB9U47BpX6"><img src="https://invidget.switchblade.xyz/AYZ7RJ3BuA"></a>
  </p>
</div>

## Requirements

- Jellyfin 12.0.0-rc2 or a compatible Jellyfin 12 build
- .NET 10 runtime support on the Jellyfin host
- Network access from Jellyfin to the configured SkipMe.db service
- Intro Skipper installed and populated locally if the Share tab is used
- Optional network access to TVMaze when sharing shows with missing external IDs

## Installation

1. Download the latest `SkipMe.db-plugin-*.zip` from the project releases.
2. Extract `SkipMe.Db.Plugin.dll` from the archive.
3. Copy the DLL into Jellyfin's plugin directory, for example:
   - Linux: `/var/lib/jellyfin/plugins/SkipMe.db/`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\SkipMe.db\`
4. Restart Jellyfin.
5. Confirm that `SkipMe.db` appears under Dashboard → Plugins.

## Synchronization

The visible `Sync SkipMe.db Segment Database` scheduled task runs in the
`Intro Skipper` category. Its default trigger is daily at 01:00 local time.
Manual and scheduled attempts are rate-limited to one attempt every eight
hours, and overlapping runs are rejected.

Each run scans non-virtual movies and TV episodes in the Jellyfin library. It:

- Uses TMDB, TVDB, IMDb, and AniList identifiers when available.
- Groups episodes into series lookups when possible.
- Falls back to an individual media lookup when a series lookup is not possible.
- Matches series timestamps to the local file duration within five seconds.
- Stores one valid timestamp per segment type for each item.
- Retries items that returned no timestamps on a later run.
- Preserves the existing cache if the run is cancelled, incomplete, or the
  remote usage limit is reached.
- Triggers Jellyfin's media-segment scan after a successful full sync.

Requests are paced and split into batches to respect the SkipMe.db service's
request limits. A temporary service failure does not remove previously cached
segments.

## Enabling the provider

Jellyfin controls media-segment providers per library:

1. Open Dashboard → Libraries → Libraries.
2. Open the desired library menu (`...`) and choose Manage library.
3. Find Media segment providers.
4. Enable `SkipMe.db` and adjust provider priority as needed.

The provider supports movies and TV episodes. Invalid or unknown timestamp
data is ignored.

## Plugin settings

Open Dashboard → Plugins → SkipMe.db.

### Skip

The Skip tab controls which locally synchronized segments Jellyfin may use.
Its displayed counts come only from SkipMe.db's local cache; Intro Skipper's
database is not used for this tab.

- Toggle an entire library, series, season, or movie.
- Expand a series to manage individual seasons.
- Season 0 (Specials) is disabled by default and must be explicitly enabled.
- Disabled items remain cached but are not surfaced to Jellyfin.
- Save changes with `Save Settings`.

Libraries where `SkipMe.db` is disabled as a Jellyfin media-segment provider
are not shown. The filter searches displayed library items, and large-library
truncation notices are shown when Jellyfin limits an item query.

### Share

The Share tab reads official segment rows from Intro Skipper's SQLite database.
Its counts show eligible timestamps that have not already been shared through
this plugin. Sharing is opt-in: items start disabled on each page load, and
only enabled movies, series, and seasons are submitted.

Sharing requires a valid media duration and at least one supported external
identifier. Show submissions use episode, season, and series metadata as
available; TVMaze may be queried to fill missing TVDB or IMDb show IDs.

The Share workflow:

- Supports movies and TV episodes.
- Uses one canonical timestamp per segment type and item.
- Excludes Intro Skipper `CreditsDerived` preview rows created by its
  after-credits preview option. Other recorded preview sources remain eligible.
- Deduplicates against local share history within one second for the item,
  segment type, start, end, and duration.
- Records successful submissions in the local share history so they are not
  repeatedly uploaded.
- Reports shared timestamps, already-shared timestamps, missing metadata, and
  items without valid Intro Skipper timestamps.

Season 0 is also disabled by default in the Share tab and must be explicitly
enabled for sharing.

## Local data

The plugin maintains a local SQLite cache for synchronized segments, sync
metadata, and share history. Older cache formats are migrated or retired when
possible. Intro Skipper's database is read-only input for the Share workflow.

## HTTP endpoints

The settings page uses these elevated Jellyfin API endpoints:

- `GET /SkipMeDb/Segments/Counts` — counts from the local SkipMe.db cache.
- `GET /SkipMeDb/Share/Counts` — unshared eligible Intro Skipper counts.
- `POST /SkipMeDb/Share` — queues the selected Share-tab items and returns a job ID.
- `GET /SkipMeDb/Share/{jobId}` — gets the status and result of a share job.

## Building from source

The project targets `net10.0` and Jellyfin 12. A SkipMe.db service URL is
required at build time through the `SkipMeApiUrl` MSBuild property or the
`API_URL` environment variable:

```powershell
dotnet build SkipMe.Db.Plugin.sln -p:SkipMeApiUrl=https://your-skipme-service.example
```

The build runs the web configuration-page build automatically. Node.js/npm are
required for that step; dependencies are installed with `npm ci` when
`web/node_modules` is absent.

## License

This project is licensed under the GPL-3.0-only license. See [LICENSE](LICENSE)
and [NOTICE](NOTICE) for details.
