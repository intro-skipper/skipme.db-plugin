// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkipMe.Db.Plugin.Models;

namespace SkipMe.Db.Plugin.Services;

/// <summary>
/// HTTP client for the SkipMe.db API at https://db.skipme.workers.dev.
/// Fetches crowd-sourced segment timestamps for TV seasons.
/// </summary>
public class SkipMeApiClient
{
    private const string BaseUrl = "https://db.skipme.workers.dev";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SkipMeApiClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkipMeApiClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public SkipMeApiClient(IHttpClientFactory httpClientFactory, ILogger<SkipMeApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches all segment timestamps for a season using the TVDB season ID.
    /// </summary>
    /// <param name="tvdbSeasonId">The TVDB season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The season response, or <c>null</c> if not found or on error.</returns>
    public Task<SeasonResponse?> GetByTvdbSeasonIdAsync(int tvdbSeasonId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/v1/season?tvdb_season_id={tvdbSeasonId}";
        return FetchSeasonAsync(url, cancellationToken);
    }

    /// <summary>
    /// Fetches all segment timestamps for a season using the TMDB series ID and season number.
    /// </summary>
    /// <param name="tmdbId">The TMDB series ID.</param>
    /// <param name="season">The season number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The season response, or <c>null</c> if not found or on error.</returns>
    public Task<SeasonResponse?> GetByTmdbIdAsync(int tmdbId, int season, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/v1/season?tmdb_id={tmdbId}&season={season}";
        return FetchSeasonAsync(url, cancellationToken);
    }

    /// <summary>
    /// Fetches all segment timestamps for a season using the AniList series ID and season number.
    /// </summary>
    /// <param name="aniListId">The AniList series ID.</param>
    /// <param name="season">The season number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The season response, or <c>null</c> if not found or on error.</returns>
    public Task<SeasonResponse?> GetByAniListIdAsync(int aniListId, int season, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/v1/season?anilist_id={aniListId}&season={season}";
        return FetchSeasonAsync(url, cancellationToken);
    }

    /// <summary>
    /// Fetches segment timestamps for a single movie or episode via the <c>/v1/media</c> endpoint.
    /// At least one of <paramref name="tmdbId"/>, <paramref name="tvdbId"/>, or <paramref name="aniListId"/> must be provided.
    /// </summary>
    /// <param name="tmdbId">The TMDB series or movie ID.</param>
    /// <param name="tvdbId">The TVDB series or movie ID.</param>
    /// <param name="aniListId">The AniList series ID.</param>
    /// <param name="season">Season number (required for TV when using <paramref name="aniListId"/> or <paramref name="tmdbId"/>).</param>
    /// <param name="episode">Episode number (required for TV when using <paramref name="aniListId"/> or <paramref name="tmdbId"/>).</param>
    /// <param name="durationMs">Episode duration in milliseconds; narrows matching to ±5 000 ms.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The media response, or <c>null</c> if not found or on error.</returns>
    public Task<MediaResponse?> GetByMediaAsync(
        int? tmdbId,
        int? tvdbId,
        int? aniListId,
        int? season,
        int? episode,
        long? durationMs,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder($"{BaseUrl}/v1/media?");
        var sep = string.Empty;

        if (tmdbId.HasValue)
        {
            sb.Append(sep).Append("tmdb_id=").Append(tmdbId.Value);
            sep = "&";
        }

        if (tvdbId.HasValue)
        {
            sb.Append(sep).Append("tvdb_id=").Append(tvdbId.Value);
            sep = "&";
        }

        if (aniListId.HasValue)
        {
            sb.Append(sep).Append("anilist_id=").Append(aniListId.Value);
            sep = "&";
        }

        if (season.HasValue)
        {
            sb.Append(sep).Append("season=").Append(season.Value);
            sep = "&";
        }

        if (episode.HasValue)
        {
            sb.Append(sep).Append("episode=").Append(episode.Value);
            sep = "&";
        }

        if (durationMs.HasValue)
        {
            sb.Append(sep).Append("duration_ms=").Append(durationMs.Value);
        }

        return FetchMediaAsync(sb.ToString(), cancellationToken);
    }

    private async Task<MediaResponse?> FetchMediaAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(SkipMeApiClient));
            var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("No media segments found at {Url}", url);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "SkipMe.db API returned {StatusCode} for {Url}",
                    (int)response.StatusCode,
                    url);
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<MediaResponse>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            _logger.LogWarning(ex, "Failed to fetch media segments from SkipMe.db API at {Url}", url);
            return null;
        }
    }

    private async Task<SeasonResponse?> FetchSeasonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(SkipMeApiClient));
            var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("No segments found at {Url}", url);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "SkipMe.db API returned {StatusCode} for {Url}",
                    (int)response.StatusCode,
                    url);
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<SeasonResponse>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            _logger.LogWarning(ex, "Failed to fetch segments from SkipMe.db API at {Url}", url);
            return null;
        }
    }
}
