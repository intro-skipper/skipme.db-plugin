// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using SkipMe.Db.Plugin.Models;
using SkipMe.Db.Plugin.Services;

namespace SkipMe.Db.Plugin.Tasks;

/// <summary>
/// Scheduled task that syncs segment timestamps from the SkipMe.db API into the local segment store
/// for every movie and TV episode present on the Jellyfin server.
/// </summary>
public class SyncSegmentsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly SkipMeApiClient _apiClient;
    private readonly SegmentStore _segmentStore;
    private readonly ILogger<SyncSegmentsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncSegmentsTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="apiClient">The SkipMe.db API client.</param>
    /// <param name="segmentStore">The local segment store.</param>
    /// <param name="logger">The logger.</param>
    public SyncSegmentsTask(
        ILibraryManager libraryManager,
        SkipMeApiClient apiClient,
        SegmentStore segmentStore,
        ILogger<SyncSegmentsTask> logger)
    {
        _libraryManager = libraryManager;
        _apiClient = apiClient;
        _segmentStore = segmentStore;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "Sync SkipMe.db Segments";

    /// <inheritdoc/>
    public string Key => "SkipMeDbSync";

    /// <inheritdoc/>
    public string Description => "Fetches crowd-sourced segment timestamps from the SkipMe.db API for all movies and TV episodes on the server and stores them locally.";

    /// <inheritdoc/>
    public string Category => "SkipMe.db";

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
            },
        ];
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var newSegments = new Dictionary<Guid, List<StoredSegment>>();

        // Collect movies and episodes present on this Jellyfin server.
        var movies = _libraryManager
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie], IsVirtualItem = false, Recursive = true })
            .OfType<Movie>()
            .ToList();

        var allEpisodes = _libraryManager
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Episode], IsVirtualItem = false, Recursive = true })
            .OfType<Episode>()
            .ToList();

        var totalItems = movies.Count + allEpisodes.Count;
        var processed = 0;

        _logger.LogInformation(
            "Starting SkipMe.db sync for {MovieCount} movie(s) and {EpisodeCount} episode(s)",
            movies.Count,
            allEpisodes.Count);

        // --- Movies: use /v1/media ---
        foreach (var movie in movies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var segments = await FetchAndBuildMovieSegmentsAsync(movie, cancellationToken).ConfigureAwait(false);
            if (segments.Count > 0)
            {
                newSegments[movie.Id] = segments;
            }

            processed++;
            ReportProgress(progress, processed, totalItems);
        }

        // --- TV episodes: /v1/season (cached per metadata key), fallback to /v1/media ---
        // Cache keyed by season identity string to avoid redundant API round-trips.
        var seasonCache = new Dictionary<string, SeasonResponse?>(StringComparer.Ordinal);

        foreach (var episode in allEpisodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (episode.IndexNumber is not { } epNum)
            {
                processed++;
                ReportProgress(progress, processed, totalItems);
                continue;
            }

            List<StoredSegment> storedSegments;

            var seasonKey = GetSeasonCacheKey(episode);
            if (seasonKey is not null)
            {
                if (!seasonCache.TryGetValue(seasonKey, out var seasonResponse))
                {
                    seasonResponse = await FetchSeasonDataAsync(episode, cancellationToken).ConfigureAwait(false);
                    seasonCache[seasonKey] = seasonResponse;
                }

                if (seasonResponse is not null)
                {
                    storedSegments = BuildStoredSegmentsFromSeason(seasonResponse.Segments, epNum);
                }
                else
                {
                    // Season endpoint returned nothing — fall back to per-episode /v1/media
                    var mediaResponse = await FetchMediaDataAsync(episode, cancellationToken).ConfigureAwait(false);
                    storedSegments = mediaResponse is not null
                        ? BuildStoredSegmentsFromMedia(mediaResponse)
                        : [];
                }
            }
            else
            {
                // No season-level metadata key available — use /v1/media directly
                var mediaResponse = await FetchMediaDataAsync(episode, cancellationToken).ConfigureAwait(false);
                storedSegments = mediaResponse is not null
                    ? BuildStoredSegmentsFromMedia(mediaResponse)
                    : [];
            }

            if (storedSegments.Count > 0)
            {
                newSegments[episode.Id] = storedSegments;
            }

            processed++;
            ReportProgress(progress, processed, totalItems);
        }

        await _segmentStore.ReplaceAllAsync(newSegments).ConfigureAwait(false);
        _logger.LogInformation(
            "SkipMe.db sync complete. Stored segments for {StoredCount} of {TotalItems} item(s).",
            newSegments.Count,
            totalItems);
    }

    // -------------------------------------------------------------------------
    // Season fetch helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a stable string key that identifies the season for API caching purposes,
    /// or <c>null</c> if no supported provider ID is available.
    /// </summary>
    private static string? GetSeasonCacheKey(Episode episode)
    {
        var season = episode.Season;
        if (season is not null
            && season.ProviderIds.TryGetValue("Tvdb", out var tvdbSeasonId)
            && !string.IsNullOrEmpty(tvdbSeasonId))
        {
            return string.Create(CultureInfo.InvariantCulture, $"tvdb_season:{tvdbSeasonId}");
        }

        var seasonNum = episode.ParentIndexNumber;
        var series = episode.Series;
        if (series is null || seasonNum is null)
        {
            return null;
        }

        if (series.ProviderIds.TryGetValue("Tmdb", out var tmdbId) && !string.IsNullOrEmpty(tmdbId))
        {
            return string.Create(CultureInfo.InvariantCulture, $"tmdb:{tmdbId}:season:{seasonNum.Value}");
        }

        if (series.ProviderIds.TryGetValue("AniList", out var aniListId) && !string.IsNullOrEmpty(aniListId))
        {
            return string.Create(CultureInfo.InvariantCulture, $"anilist:{aniListId}:season:{seasonNum.Value}");
        }

        return null;
    }

    private async Task<SeasonResponse?> FetchSeasonDataAsync(Episode episode, CancellationToken cancellationToken)
    {
        // Mode A: TVDB season ID (most specific)
        var season = episode.Season;
        if (season is not null
            && season.ProviderIds.TryGetValue("Tvdb", out var tvdbSeasonIdStr)
            && int.TryParse(tvdbSeasonIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tvdbSeasonId))
        {
            _logger.LogDebug("Fetching season via TVDB season ID {TvdbSeasonId}", tvdbSeasonId);
            var result = await _apiClient.GetByTvdbSeasonIdAsync(tvdbSeasonId, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        var seasonNumber = episode.ParentIndexNumber;
        if (seasonNumber is null)
        {
            return null;
        }

        var series = episode.Series;

        // Mode B: TMDB series ID + season number
        if (series is not null
            && series.ProviderIds.TryGetValue("Tmdb", out var tmdbIdStr)
            && int.TryParse(tmdbIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            _logger.LogDebug("Fetching season via TMDB ID {TmdbId}, season {Season}", tmdbId, seasonNumber.Value);
            var result = await _apiClient.GetByTmdbIdAsync(tmdbId, seasonNumber.Value, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        // Mode C: AniList series ID + season number
        if (series is not null
            && series.ProviderIds.TryGetValue("AniList", out var aniListIdStr)
            && int.TryParse(aniListIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aniListId))
        {
            _logger.LogDebug("Fetching season via AniList ID {AniListId}, season {Season}", aniListId, seasonNumber.Value);
            return await _apiClient.GetByAniListIdAsync(aniListId, seasonNumber.Value, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // /v1/media fetch helpers
    // -------------------------------------------------------------------------

    private async Task<List<StoredSegment>> FetchAndBuildMovieSegmentsAsync(Movie movie, CancellationToken cancellationToken)
    {
        var mediaResponse = await FetchMediaDataAsync(movie, cancellationToken).ConfigureAwait(false);
        return mediaResponse is not null ? BuildStoredSegmentsFromMedia(mediaResponse) : [];
    }

    private async Task<MediaResponse?> FetchMediaDataAsync(BaseItem item, CancellationToken cancellationToken)
    {
        int? tmdbId = null;
        int? tvdbId = null;
        int? aniListId = null;
        int? seasonNum = null;
        int? episodeNum = null;
        long? durationMs = item.RunTimeTicks.HasValue
            ? item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond
            : null;

        if (item is Episode episode)
        {
            var series = episode.Series;
            seasonNum = episode.ParentIndexNumber;
            episodeNum = episode.IndexNumber;

            if (series is not null)
            {
                if (series.ProviderIds.TryGetValue("Tmdb", out var tStr)
                    && int.TryParse(tStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tId))
                {
                    tmdbId = tId;
                }

                if (series.ProviderIds.TryGetValue("Tvdb", out var vStr)
                    && int.TryParse(vStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vId))
                {
                    tvdbId = vId;
                }

                if (series.ProviderIds.TryGetValue("AniList", out var aStr)
                    && int.TryParse(aStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aId))
                {
                    aniListId = aId;
                }
            }
        }
        else
        {
            // Movie — provider IDs are on the item itself
            if (item.ProviderIds.TryGetValue("Tmdb", out var tStr)
                && int.TryParse(tStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tId))
            {
                tmdbId = tId;
            }

            if (item.ProviderIds.TryGetValue("Tvdb", out var vStr)
                && int.TryParse(vStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vId))
            {
                tvdbId = vId;
            }

            if (item.ProviderIds.TryGetValue("AniList", out var aStr)
                && int.TryParse(aStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aId))
            {
                aniListId = aId;
            }
        }

        if (tmdbId is null && tvdbId is null && aniListId is null)
        {
            _logger.LogDebug("No supported provider ID found for item {ItemId}, skipping", item.Id);
            return null;
        }

        _logger.LogDebug(
            "Fetching media segments for item {ItemId} (tmdb={TmdbId}, tvdb={TvdbId})",
            item.Id,
            tmdbId,
            tvdbId);

        return await _apiClient
            .GetByMediaAsync(tmdbId, tvdbId, aniListId, seasonNum, episodeNum, durationMs, cancellationToken)
            .ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Segment builders
    // -------------------------------------------------------------------------

    private static List<StoredSegment> BuildStoredSegmentsFromSeason(IList<SegmentEntry> allSegments, int episodeNumber)
    {
        var segments = new List<StoredSegment>();
        var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in allSegments)
        {
            if (entry.Episode != episodeNumber)
            {
                continue;
            }

            if (string.IsNullOrEmpty(entry.Segment) || !seenTypes.Add(entry.Segment))
            {
                continue;
            }

            if (entry.StartMs <= 0 || entry.EndMs <= 0 || entry.StartMs > entry.EndMs)
            {
                continue;
            }

            segments.Add(new StoredSegment
            {
                Type = entry.Segment,
                StartMs = entry.StartMs,
                EndMs = entry.EndMs,
            });
        }

        return segments;
    }

    private static List<StoredSegment> BuildStoredSegmentsFromMedia(MediaResponse response)
    {
        var segments = new List<StoredSegment>();
        AddFirstTimestamp(segments, "intro", response.Intro);
        AddFirstTimestamp(segments, "recap", response.Recap);
        AddFirstTimestamp(segments, "credits", response.Credits);
        AddFirstTimestamp(segments, "preview", response.Preview);
        return segments;
    }

    private static void AddFirstTimestamp(
        List<StoredSegment> segments,
        string type,
        IList<MediaTimestamp> timestamps)
    {
        if (timestamps.Count == 0)
        {
            return;
        }

        var first = timestamps[0];
        if (first.EndMs is not { } endMs)
        {
            return;
        }

        if (first.StartMs <= 0 || endMs <= 0 || first.StartMs > endMs)
        {
            return;
        }

        segments.Add(new StoredSegment
        {
            Type = type,
            StartMs = first.StartMs,
            EndMs = endMs,
        });
    }

    private static void ReportProgress(IProgress<double> progress, int processed, int total)
    {
        if (total > 0)
        {
            progress.Report(100.0 * processed / total);
        }
    }
}
