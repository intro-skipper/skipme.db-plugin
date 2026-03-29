// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
using Skipme.Db.Plugin.Models;
using Skipme.Db.Plugin.Services;

namespace Skipme.Db.Plugin.Providers;

/// <summary>
/// Provides media segment timestamps sourced from the SkipMe.db crowd-sourced API.
/// </summary>
public class SegmentProvider : IMediaSegmentProvider
{
    /// <summary>
    /// Mapping from SkipMe.db segment type strings to Jellyfin <see cref="MediaSegmentType"/> values.
    /// </summary>
    private static readonly Dictionary<string, MediaSegmentType> SegmentTypeMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["intro"] = MediaSegmentType.Intro,
        ["credits"] = MediaSegmentType.Outro,
        ["recap"] = MediaSegmentType.Recap,
        ["preview"] = MediaSegmentType.Preview,
        ["commercial"] = MediaSegmentType.Commercial,
    };

    private readonly SkipMeApiClient _apiClient;
    private readonly ILogger<SegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentProvider"/> class.
    /// </summary>
    /// <param name="apiClient">The SkipMe.db API client.</param>
    /// <param name="logger">The logger.</param>
    public SegmentProvider(SkipMeApiClient apiClient, ILogger<SegmentProvider> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => Plugin.Instance!.Name;

    /// <inheritdoc/>
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var libraryManager = Plugin.Instance!.LibraryManager;
        if (libraryManager.GetItemById(request.ItemId) is not Episode episode)
        {
            return [];
        }

        var episodeNumber = episode.IndexNumber;
        if (episodeNumber is null)
        {
            _logger.LogDebug("Episode {ItemId} has no index number, skipping", request.ItemId);
            return [];
        }

        var seasonResponse = await FetchSeasonDataAsync(episode, cancellationToken).ConfigureAwait(false);
        if (seasonResponse is null)
        {
            return [];
        }

        return BuildSegments(request.ItemId, episodeNumber.Value, seasonResponse.Segments);
    }

    private async Task<SeasonResponse?> FetchSeasonDataAsync(Episode episode, CancellationToken cancellationToken)
    {
        // Mode A: TVDB season ID (most specific — use first)
        var season = episode.Season;
        if (season is not null
            && season.ProviderIds.TryGetValue("Tvdb", out var tvdbSeasonIdStr)
            && int.TryParse(tvdbSeasonIdStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tvdbSeasonId))
        {
            _logger.LogDebug("Fetching segments via TVDB season ID {TvdbSeasonId}", tvdbSeasonId);
            var result = await _apiClient.GetByTvdbSeasonIdAsync(tvdbSeasonId, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        var seasonNumber = episode.ParentIndexNumber;
        if (seasonNumber is null)
        {
            _logger.LogDebug("Episode has no season number, cannot fall back to TMDB/AniList lookup");
            return null;
        }

        var series = episode.Series;

        // Mode B: TMDB series ID + season number
        if (series is not null
            && series.ProviderIds.TryGetValue("Tmdb", out var tmdbIdStr)
            && int.TryParse(tmdbIdStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tmdbId))
        {
            _logger.LogDebug("Fetching segments via TMDB ID {TmdbId}, season {Season}", tmdbId, seasonNumber.Value);
            var result = await _apiClient.GetByTmdbIdAsync(tmdbId, seasonNumber.Value, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        // Mode C: AniList series ID + season number
        if (series is not null
            && series.ProviderIds.TryGetValue("AniList", out var aniListIdStr)
            && int.TryParse(aniListIdStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var aniListId))
        {
            _logger.LogDebug("Fetching segments via AniList ID {AniListId}, season {Season}", aniListId, seasonNumber.Value);
            return await _apiClient.GetByAniListIdAsync(aniListId, seasonNumber.Value, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("No supported provider ID found for episode {ItemId}", episode.Id);
        return null;
    }

    private List<MediaSegmentDto> BuildSegments(
        Guid itemId,
        int episodeNumber,
        IList<SegmentEntry> allSegments)
    {
        var segments = new List<MediaSegmentDto>();
        var seenTypes = new HashSet<MediaSegmentType>();

        foreach (var entry in allSegments)
        {
            if (entry.Episode != episodeNumber)
            {
                continue;
            }

            if (!SegmentTypeMappings.TryGetValue(entry.Segment, out var segmentType))
            {
                _logger.LogDebug("Unknown segment type '{SegmentType}' — skipping", entry.Segment);
                continue;
            }

            if (entry.EndMs <= 0)
            {
                continue;
            }

            // Allow at most one segment per type per episode (skip duplicates)
            if (!seenTypes.Add(segmentType))
            {
                continue;
            }

            segments.Add(new MediaSegmentDto
            {
                ItemId = itemId,
                Type = segmentType,
                StartTicks = entry.StartMs * TimeSpan.TicksPerMillisecond,
                EndTicks = entry.EndMs * TimeSpan.TicksPerMillisecond,
            });
        }

        return segments;
    }
}
