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
    /// <summary>Maximum duration difference in milliseconds for a series segment to match a media file (±5 s).</summary>
    private const long SeriesDurationToleranceMs = 5000;
    private const string MediaSegmentScanTaskKey = "TaskExtractMediaSegments";

    private static readonly TimeSpan MinimumSyncInterval = TimeSpan.FromDays(1);
    private static readonly SemaphoreSlim SyncExecutionGate = new(1, 1);
    private static readonly TaskOptions DefaultTaskOptions = new();

    private readonly ILibraryManager _libraryManager;
    private readonly SkipMeApiClient _apiClient;
    private readonly SegmentStore _segmentStore;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<SyncSegmentsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncSegmentsTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="apiClient">The SkipMe.db API client.</param>
    /// <param name="segmentStore">The local segment store.</param>
    /// <param name="taskManager">The Jellyfin task manager, used to trigger the media segment scan after syncing.</param>
    /// <param name="logger">The logger.</param>
    public SyncSegmentsTask(
        ILibraryManager libraryManager,
        SkipMeApiClient apiClient,
        SegmentStore segmentStore,
        ITaskManager taskManager,
        ILogger<SyncSegmentsTask> logger)
    {
        _libraryManager = libraryManager;
        _apiClient = apiClient;
        _segmentStore = segmentStore;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "Sync SkipMe.db Segment Database";

    /// <inheritdoc/>
    public string Key => "SkipMeDbSync";

    /// <inheritdoc/>
    public string Description => "Fetches relevant crowd-sourced segment timestamps from the SkipMe.db API and stores them locally.";

    /// <inheritdoc/>
    public string Category => "Intro Skipper";

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(1).Ticks,
            },
        ];
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await SyncExecutionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("SkipMe.db sync is already running; skipping overlapping run.");
            return;
        }

        try
        {
            var lastSuccessfulSync = _segmentStore.GetLastSuccessfulSyncUtc();
            var now = DateTimeOffset.UtcNow;
            if (lastSuccessfulSync.HasValue && (now - lastSuccessfulSync.Value) < MinimumSyncInterval)
            {
                _logger.LogWarning(
                    "Skipping SkipMe.db sync: exceeding the rate limit of one run per day (last successful run at {LastRunUtc:o}).",
                    lastSuccessfulSync.Value);
                return;
            }

            var newSegments = new Dictionary<Guid, List<StoredSegment>>();

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
            var movieLookups = new List<MovieLookupWorkItem>();
            var showLookupMap = new Dictionary<string, ShowLookupWorkItem>(StringComparer.Ordinal);

            _logger.LogInformation(
                "Starting SkipMe.db sync for {MovieCount} movie(s) and {EpisodeCount} episode(s)",
                movies.Count,
                allEpisodes.Count);

            foreach (var movie in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = BuildMovieLookupRequest(movie);
                if (request is not null)
                {
                    movieLookups.Add(new MovieLookupWorkItem(movie.Id, request));
                }

                processed++;
                ReportProgress(progress, processed, totalItems);
            }

            foreach (var episode in allEpisodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (episode.IndexNumber is { } episodeNumber && episode.ParentIndexNumber is { } seasonNumber)
                {
                    if (TryBuildShowLookup(episode, out var showKey, out var showRequest))
                    {
                        if (!showLookupMap.TryGetValue(showKey, out var showWorkItem))
                        {
                            showWorkItem = new ShowLookupWorkItem(showRequest);
                            showLookupMap[showKey] = showWorkItem;
                        }

                        showWorkItem.Episodes.Add(
                            new EpisodeSeriesWorkItem(
                                episode.Id,
                                seasonNumber,
                                episodeNumber,
                                GetDurationMs(episode)));
                    }
                    else
                    {
                        var request = BuildMovieLookupRequest(episode);
                        if (request is not null)
                        {
                            movieLookups.Add(new MovieLookupWorkItem(episode.Id, request));
                        }
                    }
                }

                processed++;
                ReportProgress(progress, processed, totalItems);
            }

            var movieRequests = movieLookups.Select(w => w.Request).ToList();
            var movieResponses = await _apiClient.GetByMoviesBatchAsync(movieRequests, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < movieLookups.Count && i < movieResponses.Count; i++)
            {
                var response = movieResponses[i];
                if (response is null)
                {
                    continue;
                }

                var segments = BuildStoredSegmentsFromMedia(response);
                if (segments.Count > 0)
                {
                    newSegments[movieLookups[i].ItemId] = segments;
                }
            }

            var showLookups = showLookupMap.Values.ToList();
            var showRequests = showLookups.Select(w => w.Request).ToList();
            var showResponses = await _apiClient.GetByShowsBatchAsync(showRequests, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < showLookups.Count && i < showResponses.Count; i++)
            {
                var showResponse = showResponses[i];
                if (showResponse is null)
                {
                    continue;
                }

                foreach (var episode in showLookups[i].Episodes)
                {
                    var segments = BuildStoredSegmentsFromSeries(
                        showResponse.Segments,
                        episode.SeasonNumber,
                        episode.EpisodeNumber,
                        episode.DurationMs);
                    if (segments.Count > 0)
                    {
                        newSegments[episode.ItemId] = segments;
                    }
                }
            }

            await _segmentStore.ReplaceAllAsync(newSegments).ConfigureAwait(false);
            await _segmentStore.SetLastSuccessfulSyncUtcAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);

            _logger.LogInformation(
                "SkipMe.db sync complete. Stored segments for {StoredCount} of {TotalItems} item(s).",
                newSegments.Count,
                totalItems);

            TriggerMediaSegmentScan();
        }
        finally
        {
            SyncExecutionGate.Release();
        }
    }

    private void TriggerMediaSegmentScan()
    {
        var worker = _taskManager.ScheduledTasks
            .FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, MediaSegmentScanTaskKey, StringComparison.Ordinal));

        if (worker is null)
        {
            _logger.LogWarning(
                "Could not find scheduled task with key '{TaskKey}' — media segment scan will not be triggered",
                MediaSegmentScanTaskKey);
            return;
        }

        _logger.LogInformation("Queuing Jellyfin media segment scan ('{TaskKey}')", MediaSegmentScanTaskKey);
        _taskManager.QueueScheduledTask(worker.ScheduledTask, DefaultTaskOptions);
    }

    private static bool TryBuildShowLookup(Episode episode, out string key, out ShowLookupRequest request)
    {
        key = string.Empty;
        request = new ShowLookupRequest();

        var series = episode.Series;
        if (series is null)
        {
            return false;
        }

        var tvdbSeriesId = TryGetIntProviderId(series, "Tvdb");
        var tmdbId = TryGetIntProviderId(series, "Tmdb");
        var imdbSeriesId = TryGetStringProviderId(series, "Imdb");
        var aniListId = TryGetIntProviderId(series, "AniList");

        if (tvdbSeriesId is null && tmdbId is null && imdbSeriesId is null && aniListId is null)
        {
            return false;
        }

        if (tvdbSeriesId is not null)
        {
            key = string.Create(CultureInfo.InvariantCulture, $"tvdb_series:{tvdbSeriesId.Value}");
        }
        else if (tmdbId is not null)
        {
            key = string.Create(CultureInfo.InvariantCulture, $"tmdb:{tmdbId.Value}");
        }
        else if (imdbSeriesId is not null)
        {
            key = string.Create(CultureInfo.InvariantCulture, $"imdb:{imdbSeriesId}");
        }
        else
        {
            key = string.Create(CultureInfo.InvariantCulture, $"anilist:{aniListId!.Value}");
        }

        request = new ShowLookupRequest
        {
            TvdbSeriesId = tvdbSeriesId,
            TmdbId = tmdbId,
            ImdbSeriesId = imdbSeriesId,
            AniListId = aniListId,
        };

        return true;
    }

    private MovieLookupRequest? BuildMovieLookupRequest(BaseItem item)
    {
        var durationMs = GetDurationMs(item);
        if (durationMs is null)
        {
            _logger.LogDebug("Duration unknown for item {ItemId}, skipping movies endpoint", item.Id);
            return null;
        }

        int? tmdbId;
        int? tvdbId;
        int? aniListId;
        string? imdbId;
        int? season = null;
        int? episode = null;

        if (item is Episode episodeItem)
        {
            tmdbId = TryGetIntProviderId(episodeItem.Series, "Tmdb");
            tvdbId = TryGetIntProviderId(episodeItem, "Tvdb");
            aniListId = TryGetIntProviderId(episodeItem.Series, "AniList");
            imdbId = TryGetStringProviderId(episodeItem.Series, "Imdb");
            season = episodeItem.ParentIndexNumber;
            episode = episodeItem.IndexNumber;
        }
        else
        {
            tmdbId = TryGetIntProviderId(item, "Tmdb");
            tvdbId = TryGetIntProviderId(item, "Tvdb");
            aniListId = TryGetIntProviderId(item, "AniList");
            imdbId = TryGetStringProviderId(item, "Imdb");
        }

        if (tmdbId is null && tvdbId is null && aniListId is null && imdbId is null)
        {
            _logger.LogDebug("No supported provider ID found for item {ItemId}, skipping", item.Id);
            return null;
        }

        return new MovieLookupRequest
        {
            TmdbId = tmdbId,
            ImdbId = imdbId,
            TvdbId = tvdbId,
            AniListId = aniListId,
            Season = season,
            Episode = episode,
            DurationMs = durationMs.Value,
        };
    }

    private static int? TryGetIntProviderId(BaseItem? item, string provider)
    {
        if (item?.ProviderIds.TryGetValue(provider, out var raw) == true
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? TryGetStringProviderId(BaseItem? item, string provider)
    {
        return item?.ProviderIds.TryGetValue(provider, out var raw) == true && !string.IsNullOrWhiteSpace(raw)
            ? raw
            : null;
    }

    private static long? GetDurationMs(BaseItem item)
    {
        return item.RunTimeTicks.HasValue
            ? item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond
            : null;
    }

    // -------------------------------------------------------------------------
    // Segment builders
    // -------------------------------------------------------------------------

    private static List<StoredSegment> BuildStoredSegmentsFromSeries(IList<SegmentEntry> allSegments, int seasonNumber, int episodeNumber, long? durationMs)
    {
        var segments = new List<StoredSegment>();
        var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in allSegments)
        {
            if (entry.Season != seasonNumber || entry.Episode != episodeNumber)
            {
                continue;
            }

            // Only use segments whose recorded duration is within the tolerance of this media file's
            // actual duration so that entries submitted for a different cut of the episode are excluded.
            if (durationMs.HasValue && Math.Abs(entry.DurationMs - durationMs.Value) > SeriesDurationToleranceMs)
            {
                continue;
            }

            if (string.IsNullOrEmpty(entry.Segment) || !seenTypes.Add(entry.Segment))
            {
                continue;
            }

            if (entry.StartMs < 0 || entry.EndMs <= 0 || entry.StartMs >= entry.EndMs)
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

        if (first.StartMs < 0 || endMs <= 0 || first.StartMs >= endMs)
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

    private sealed record MovieLookupWorkItem(Guid ItemId, MovieLookupRequest Request);

    private sealed class ShowLookupWorkItem
    {
        public ShowLookupWorkItem(ShowLookupRequest request)
        {
            Request = request;
        }

        public ShowLookupRequest Request { get; }

        public List<EpisodeSeriesWorkItem> Episodes { get; } = [];
    }

    private sealed record EpisodeSeriesWorkItem(Guid ItemId, int SeasonNumber, int EpisodeNumber, long? DurationMs);
}
