// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SkipMe.Db.Plugin.Models;

namespace SkipMe.Db.Plugin.Services;

/// <summary>
/// Builds and submits share payloads from Intro Skipper timestamps.
/// </summary>
public sealed class ShareSubmissionService
{
    private const string BaseUrl = "https://db.skipme.workers.dev";

    private readonly ILibraryManager _libraryManager;
    private readonly SegmentStore _segmentStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ShareSubmissionService> _logger;
    private readonly string _introSkipperDbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareSubmissionService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="segmentStore">Local SkipMe segment store.</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="applicationPaths">Application path provider.</param>
    /// <param name="logger">Logger.</param>
    public ShareSubmissionService(
        ILibraryManager libraryManager,
        SegmentStore segmentStore,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILogger<ShareSubmissionService> logger)
    {
        _libraryManager = libraryManager;
        _segmentStore = segmentStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _introSkipperDbPath = Path.Combine(applicationPaths.DataPath, "introskipper", "introskipper.db");
    }

    /// <summary>
    /// Shares enabled filtered items.
    /// </summary>
    /// <param name="request">Share request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Share run summary.</returns>
    public async Task<ShareSubmitResponse> ShareAsync(ShareSubmitRequest request, CancellationToken cancellationToken)
    {
        var filteredSeriesIds = ParseGuidSet(request.FilteredSeriesIds);
        var filteredMovieIds = ParseGuidSet(request.FilteredMovieIds);
        var disabledSeriesIds = ParseGuidSet(request.DisabledSeriesIds);
        var disabledSeasonIds = ParseGuidSet(request.DisabledSeasonIds);
        var disabledMovieIds = ParseGuidSet(request.DisabledMovieIds);
        var enabledSpecialsSeasonIds = ParseGuidSet(request.EnabledSpecialsSeasonIds);

        if (filteredSeriesIds.Count == 0 && filteredMovieIds.Count == 0)
        {
            return new ShareSubmitResponse { Ok = true };
        }

        var skippedMissingMetadata = 0;
        var skippedNoSegments = 0;

        var movieCandidates = BuildMovieCandidates(filteredMovieIds, disabledMovieIds, ref skippedMissingMetadata);
        var showCandidates = BuildShowCandidates(filteredSeriesIds, disabledSeriesIds, disabledSeasonIds, enabledSpecialsSeasonIds, ref skippedMissingMetadata);

        var allCandidateItemIds = movieCandidates.Select(m => m.ItemId)
            .Concat(showCandidates.Select(s => s.ItemId))
            .Distinct()
            .ToList();

        var introSegmentsByItemId = LoadIntroSkipperSegments(allCandidateItemIds);

        var seasonRequests = BuildSeasonPayload(showCandidates, introSegmentsByItemId, ref skippedNoSegments);
        var movieRequests = BuildMoviePayload(movieCandidates, introSegmentsByItemId, ref skippedNoSegments);

        var allFingerprints = seasonRequests.SelectMany(s => s.Items).Select(i => i.Fingerprint)
            .Concat(movieRequests.Select(m => m.Fingerprint))
            .ToList();

        if (allFingerprints.Count == 0)
        {
            return new ShareSubmitResponse
            {
                Ok = true,
                SkippedMissingMetadata = skippedMissingMetadata,
                SkippedNoSegments = skippedNoSegments,
            };
        }

        var dedupedFingerprints = _segmentStore.GetUnsharedFingerprints(allFingerprints);
        var dedupedSet = new HashSet<SharedUploadFingerprint>(dedupedFingerprints);
        var skippedAlreadyShared = allFingerprints.Count - dedupedSet.Count;

        foreach (var season in seasonRequests)
        {
            season.Items = [.. season.Items.Where(i => dedupedSet.Contains(i.Fingerprint))];
        }

        seasonRequests = [.. seasonRequests.Where(s => s.Items.Count > 0)];
        movieRequests = [.. movieRequests.Where(m => dedupedSet.Contains(m.Fingerprint))];

        if (seasonRequests.Count == 0 && movieRequests.Count == 0)
        {
            return new ShareSubmitResponse
            {
                Ok = true,
                SkippedAlreadyShared = skippedAlreadyShared,
                SkippedMissingMetadata = skippedMissingMetadata,
                SkippedNoSegments = skippedNoSegments,
            };
        }

        var ok = false; // Set true by any batch that succeeds; preserves partial success.
        var sharedSegments = 0;
        var sharedShowSeasons = 0;
        var sharedMovies = 0;
        var errors = new List<string>();
        var http = _httpClientFactory.CreateClient(nameof(SkipMeApiClient));

        if (seasonRequests.Count > 0)
        {
            var seasonResult = await SubmitAsync(http, "/v1/submit/season", seasonRequests, cancellationToken).ConfigureAwait(false);
            if (seasonResult.Ok)
            {
                ok = true;
                sharedSegments += seasonResult.Submitted;
                sharedShowSeasons = seasonRequests.Count;
                // Record immediately so a crash before movie submission does not cause re-submission.
                var seasonFingerprints = seasonRequests.SelectMany(s => s.Items).Select(i => i.Fingerprint).ToList();
                await _segmentStore.RecordSharedFingerprintsAsync(seasonFingerprints).ConfigureAwait(false);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(seasonResult.Error))
                {
                    errors.Add($"Season share failed: {seasonResult.Error}");
                }
            }
        }

        if (movieRequests.Count > 0)
        {
            var movieResult = await SubmitAsync(http, "/v1/submit/collection", movieRequests, cancellationToken).ConfigureAwait(false);
            if (movieResult.Ok)
            {
                ok = true;
                sharedSegments += movieResult.Submitted;
                sharedMovies = movieRequests.Count;
                // Record immediately so state is durable even if the response is lost.
                var movieFingerprints = movieRequests.Select(m => m.Fingerprint).ToList();
                await _segmentStore.RecordSharedFingerprintsAsync(movieFingerprints).ConfigureAwait(false);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(movieResult.Error))
                {
                    errors.Add($"Movie share failed: {movieResult.Error}");
                }
            }
        }

        return new ShareSubmitResponse
        {
            Ok = ok,
            SharedSegments = sharedSegments,
            SharedShowSeasons = sharedShowSeasons,
            SharedMovies = sharedMovies,
            SkippedAlreadyShared = skippedAlreadyShared,
            SkippedMissingMetadata = skippedMissingMetadata,
            SkippedNoSegments = skippedNoSegments,
            Error = errors.Count > 0 ? string.Join(" ", errors) : null,
        };
    }

    private static HashSet<Guid> ParseGuidSet(IEnumerable<string>? raw)
    {
        var result = new HashSet<Guid>();
        if (raw is null)
        {
            return result;
        }

        foreach (var value in raw)
        {
            if (Guid.TryParse(value, out var id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private List<MovieCandidate> BuildMovieCandidates(HashSet<Guid> filteredMovieIds, HashSet<Guid> disabledMovieIds, ref int skippedMissingMetadata)
    {
        var result = new List<MovieCandidate>();

        foreach (var movieId in filteredMovieIds)
        {
            if (disabledMovieIds.Contains(movieId))
            {
                continue;
            }

            if (_libraryManager.GetItemById(movieId) is not Movie movie)
            {
                continue;
            }

            if (!TryGetDurationMs(movie, out var durationMs))
            {
                skippedMissingMetadata++;
                continue;
            }

            var ids = BuildIdentifiers(movie);
            if (!HasMovieMatchingStrategy(ids))
            {
                skippedMissingMetadata++;
                continue;
            }

            result.Add(new MovieCandidate(movie.Id, durationMs, ids));
        }

        return result;
    }

    private List<ShowCandidate> BuildShowCandidates(
        HashSet<Guid> filteredSeriesIds,
        HashSet<Guid> disabledSeriesIds,
        HashSet<Guid> disabledSeasonIds,
        HashSet<Guid> enabledSpecialsSeasonIds,
        ref int skippedMissingMetadata)
    {
        if (filteredSeriesIds.Count == 0)
        {
            return [];
        }

        // Scope the query to only the filtered series so we don't scan the entire library.
        var episodes = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Episode],
                IsVirtualItem = false,
                Recursive = true,
                AncestorIds = [.. filteredSeriesIds],
            })
            .OfType<Episode>();

        var candidates = new List<ShowCandidate>();

        foreach (var episode in episodes)
        {
            var seriesId = episode.Series?.Id ?? Guid.Empty;
            if (seriesId == Guid.Empty || !filteredSeriesIds.Contains(seriesId) || disabledSeriesIds.Contains(seriesId))
            {
                continue;
            }

            var seasonId = episode.ParentId;
            if (seasonId == Guid.Empty)
            {
                skippedMissingMetadata++;
                continue;
            }

            if (episode.ParentIndexNumber == 0 && !enabledSpecialsSeasonIds.Contains(seasonId))
            {
                continue;
            }

            if (disabledSeasonIds.Contains(seasonId))
            {
                continue;
            }

            if (episode.IndexNumber is not { } episodeNumber || episode.ParentIndexNumber is not { } seasonNumber)
            {
                skippedMissingMetadata++;
                continue;
            }

            if (!TryGetDurationMs(episode, out var durationMs))
            {
                skippedMissingMetadata++;
                continue;
            }

            var season = _libraryManager.GetItemById<Season>(seasonId);
            var episodeIds = BuildIdentifiers(episode);
            var seasonIds = BuildIdentifiers(season);
            var seriesIds = BuildIdentifiers(episode.Series);

            var seasonMeta = new SeasonMetadata(
                seasonId,
                seasonNumber,
                seasonIds.TvdbId,
                seriesIds.TvdbId,
                seriesIds.TmdbId,
                seriesIds.ImdbId,
                seriesIds.AniListId);

            if (!HasSeasonMatchingStrategy(seasonMeta, episodeIds))
            {
                skippedMissingMetadata++;
                continue;
            }

            candidates.Add(new ShowCandidate(episode.Id, seasonMeta, episodeNumber, durationMs, episodeIds));
        }

        return candidates;
    }

    private List<SeasonSubmitRequest> BuildSeasonPayload(
        IReadOnlyList<ShowCandidate> candidates,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, SegmentRange>> introSegments,
        ref int skippedNoSegments)
    {
        var bySeason = new Dictionary<Guid, SeasonSubmitRequest>();

        foreach (var candidate in candidates)
        {
            if (!introSegments.TryGetValue(candidate.ItemId, out var segments) || segments.Count == 0)
            {
                skippedNoSegments++;
                continue;
            }

            if (!bySeason.TryGetValue(candidate.Season.SeasonId, out var seasonRequest))
            {
                seasonRequest = new SeasonSubmitRequest
                {
                    TvdbSeriesId = candidate.Season.TvdbSeriesId,
                    TvdbSeasonId = candidate.Season.TvdbSeasonId,
                    TmdbId = candidate.Season.TmdbId,
                    ImdbSeriesId = candidate.Season.ImdbSeriesId,
                    AniListId = candidate.Season.AniListId,
                    Season = candidate.Season.SeasonNumber,
                };
                bySeason[candidate.Season.SeasonId] = seasonRequest;
            }

            foreach (var (segment, range) in segments)
            {
                seasonRequest.Items.Add(new SeasonSubmitItem
                {
                    TvdbId = candidate.Identifiers.TvdbId,
                    ImdbId = candidate.Identifiers.ImdbId,
                    Episode = candidate.EpisodeNumber,
                    Segment = segment,
                    DurationMs = candidate.DurationMs,
                    StartMs = range.StartMs,
                    EndMs = range.EndMs,
                    Fingerprint = new SharedUploadFingerprint(candidate.ItemId, segment, range.StartMs, range.EndMs, candidate.DurationMs),
                });
            }
        }

        return [.. bySeason.Values];
    }

    private List<CollectionSubmitRequest> BuildMoviePayload(
        IReadOnlyList<MovieCandidate> candidates,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, SegmentRange>> introSegments,
        ref int skippedNoSegments)
    {
        var requests = new List<CollectionSubmitRequest>();

        foreach (var candidate in candidates)
        {
            if (!introSegments.TryGetValue(candidate.ItemId, out var segments) || segments.Count == 0)
            {
                skippedNoSegments++;
                continue;
            }

            foreach (var (segment, range) in segments)
            {
                requests.Add(new CollectionSubmitRequest
                {
                    TmdbId = candidate.Identifiers.TmdbId,
                    ImdbId = candidate.Identifiers.ImdbId,
                    TvdbId = candidate.Identifiers.TvdbId,
                    AniListId = candidate.Identifiers.AniListId,
                    Segment = segment,
                    DurationMs = candidate.DurationMs,
                    StartMs = range.StartMs,
                    EndMs = range.EndMs,
                    Fingerprint = new SharedUploadFingerprint(candidate.ItemId, segment, range.StartMs, range.EndMs, candidate.DurationMs),
                });
            }
        }

        return requests;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "IN-clause placeholders are generated internally and each value is parameterized.")]
    private Dictionary<Guid, IReadOnlyDictionary<string, SegmentRange>> LoadIntroSkipperSegments(List<Guid> itemIds)
    {
        var result = new Dictionary<Guid, Dictionary<string, SegmentRange>>();

        if (itemIds.Count == 0 || !File.Exists(_introSkipperDbPath))
        {
            return [];
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _introSkipperDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        const int chunkSize = 400;
        for (var offset = 0; offset < itemIds.Count; offset += chunkSize)
        {
            var chunk = itemIds.Skip(offset).Take(chunkSize).ToList();
            using var command = connection.CreateCommand();
            var placeholders = new List<string>(chunk.Count);

            for (var i = 0; i < chunk.Count; i++)
            {
                var parameterName = $"@item{i}";
                placeholders.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, chunk[i].ToString());
            }

            command.CommandText = $"""
                SELECT ItemId, Type, Start, End
                FROM DbSegment
                WHERE Type IN (0, 1, 2, 3)
                  AND ItemId IN ({string.Join(",", placeholders)})
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var itemId))
                {
                    continue;
                }

                if (!TryMapSegmentType(reader.GetInt32(1), out var segment))
                {
                    continue;
                }

                var startMs = (long)Math.Round(reader.GetDouble(2) * 1000.0, MidpointRounding.AwayFromZero);
                var endMs = (long)Math.Round(reader.GetDouble(3) * 1000.0, MidpointRounding.AwayFromZero);
                if (startMs < 0 || endMs <= startMs)
                {
                    continue;
                }

                if (!result.TryGetValue(itemId, out var perType))
                {
                    perType = new Dictionary<string, SegmentRange>(StringComparer.OrdinalIgnoreCase);
                    result[itemId] = perType;
                }

                // Intro Skipper settings pages use the earliest segment per type, so mirror that selection.
                if (!perType.TryGetValue(segment, out var existing) || startMs < existing.StartMs)
                {
                    perType[segment] = new SegmentRange(startMs, endMs);
                }
            }
        }

        return result.ToDictionary(k => k.Key, v => (IReadOnlyDictionary<string, SegmentRange>)v.Value);
    }

    private static bool TryMapSegmentType(int type, out string segment)
    {
        segment = type switch
        {
            0 => "intro",
            1 => "credits",
            2 => "preview",
            3 => "recap",
            _ => string.Empty,
        };

        return !string.IsNullOrEmpty(segment);
    }

    private static bool TryGetDurationMs(BaseItem item, out long durationMs)
    {
        if (item.RunTimeTicks is { } runTimeTicks)
        {
            durationMs = runTimeTicks / TimeSpan.TicksPerMillisecond;
            return durationMs > 0;
        }

        durationMs = 0;
        return false;
    }

    private static ProviderIdentifiers BuildIdentifiers(BaseItem? item)
    {
        return new ProviderIdentifiers(
            TryGetIntProviderId(item, "Tvdb"),
            TryGetIntProviderId(item, "Tmdb"),
            TryGetStringProviderId(item, "Imdb"),
            TryGetIntProviderId(item, "AniList"));
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

    private static bool HasMovieMatchingStrategy(ProviderIdentifiers ids)
    {
        return ids.TvdbId is not null || ids.ImdbId is not null || ids.TmdbId is not null || ids.AniListId is not null;
    }

    private static bool HasSeasonMatchingStrategy(SeasonMetadata season, ProviderIdentifiers episode)
    {
        return season.TvdbSeriesId is not null
            || season.TvdbSeasonId is not null
            || season.TmdbId is not null
            || season.ImdbSeriesId is not null
            || season.AniListId is not null
            || episode.TvdbId is not null
            || episode.ImdbId is not null;
    }

    private async Task<SubmitResult> SubmitAsync<TRequest>(HttpClient client, string path, TRequest payload, CancellationToken cancellationToken)
    {
        var url = new Uri($"{BaseUrl}{path}");

        try
        {
            using var httpResponse = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
            if (!httpResponse.IsSuccessStatusCode)
            {
                var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return new SubmitResult(false, 0, $"HTTP {(int)httpResponse.StatusCode}: {body}");
            }

            var payloadResponse = await httpResponse.Content.ReadFromJsonAsync<SubmitResponse>(cancellationToken).ConfigureAwait(false);
            return new SubmitResult(true, payloadResponse?.Submitted ?? 0, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning(ex, "Share submission failed for {Path}", path);
            return new SubmitResult(false, 0, ex.Message);
        }
    }

    private sealed record ProviderIdentifiers(int? TvdbId, int? TmdbId, string? ImdbId, int? AniListId);

    private sealed record SegmentRange(long StartMs, long EndMs);

    private sealed record SeasonMetadata(
        Guid SeasonId,
        int SeasonNumber,
        int? TvdbSeasonId,
        int? TvdbSeriesId,
        int? TmdbId,
        string? ImdbSeriesId,
        int? AniListId);

    private sealed record ShowCandidate(Guid ItemId, SeasonMetadata Season, int EpisodeNumber, long DurationMs, ProviderIdentifiers Identifiers);

    private sealed record MovieCandidate(Guid ItemId, long DurationMs, ProviderIdentifiers Identifiers);

    private sealed record SubmitResult(bool Ok, int Submitted, string? Error);

    private sealed class SubmitResponse
    {
        [JsonPropertyName("submitted")]
        public int Submitted { get; set; }
    }

    private sealed class SeasonSubmitRequest
    {
        [JsonPropertyName("tvdb_series_id")]
        public int? TvdbSeriesId { get; set; }

        [JsonPropertyName("tvdb_season_id")]
        public int? TvdbSeasonId { get; set; }

        [JsonPropertyName("tmdb_id")]
        public int? TmdbId { get; set; }

        [JsonPropertyName("imdb_series_id")]
        public string? ImdbSeriesId { get; set; }

        [JsonPropertyName("anilist_id")]
        public int? AniListId { get; set; }

        [JsonPropertyName("season")]
        public int? Season { get; set; }

        [JsonPropertyName("items")]
        public List<SeasonSubmitItem> Items { get; set; } = [];
    }

    private sealed class SeasonSubmitItem
    {
        [JsonPropertyName("tvdb_id")]
        public int? TvdbId { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("episode")]
        public int Episode { get; set; }

        [JsonPropertyName("segment")]
        public string Segment { get; set; } = string.Empty;

        [JsonPropertyName("duration_ms")]
        public long DurationMs { get; set; }

        [JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long EndMs { get; set; }

        [JsonIgnore]
        public SharedUploadFingerprint Fingerprint { get; set; } = new(Guid.Empty, string.Empty, 0, 0, 0);
    }

    private sealed class CollectionSubmitRequest
    {
        [JsonPropertyName("tmdb_id")]
        public int? TmdbId { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        public int? TvdbId { get; set; }

        [JsonPropertyName("anilist_id")]
        public int? AniListId { get; set; }

        [JsonPropertyName("segment")]
        public string Segment { get; set; } = string.Empty;

        [JsonPropertyName("duration_ms")]
        public long DurationMs { get; set; }

        [JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long EndMs { get; set; }

        [JsonIgnore]
        public SharedUploadFingerprint Fingerprint { get; set; } = new(Guid.Empty, string.Empty, 0, 0, 0);
    }
}
