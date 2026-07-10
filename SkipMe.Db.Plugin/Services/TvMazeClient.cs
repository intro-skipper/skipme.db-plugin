// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkipMe.Db.Plugin.Models;

namespace SkipMe.Db.Plugin.Services;

/// <summary>
/// HTTP client for the TVMaze public API, used to fill in missing external IDs (TVDB, IMDb) for shows.
/// Results are cached in memory by Jellyfin series ID to avoid redundant lookups across seasons and episodes.
/// </summary>
public sealed class TvMazeClient
{
    private const string BaseUrl = "https://api.tvmaze.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TvMazeClient> _logger;

    // Absent key  = not yet attempted.
    // Present key, null value  = looked up, no match found.
    // Present key, non-null value = looked up, IDs returned.
    private readonly ConcurrentDictionary<Guid, TvMazeShowIds?> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TvMazeClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public TvMazeClient(IHttpClientFactory httpClientFactory, ILogger<TvMazeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns external IDs (TVDB series ID, IMDb ID) for the given series, fetching from TVMaze if not already cached.
    /// The result (including a not-found outcome) is stored in memory keyed by <paramref name="jellyfinSeriesId"/>
    /// so subsequent calls for the same series are served without a network round-trip.
    /// </summary>
    /// <param name="jellyfinSeriesId">The Jellyfin series item ID, used as the cache key.</param>
    /// <param name="seriesName">Display name of the series, forwarded to the TVMaze search query.</param>
    /// <param name="productionYear">Optional production year used to validate the TVMaze result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matched IDs, or <c>null</c> if no confident match was found.</returns>
    public async Task<TvMazeShowIds?> GetShowIdsAsync(
        Guid jellyfinSeriesId,
        string seriesName,
        int? productionYear,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(jellyfinSeriesId, out var cached))
        {
            return cached;
        }

        var result = await FetchShowIdsAsync(seriesName, productionYear, cancellationToken).ConfigureAwait(false);
        _cache.TryAdd(jellyfinSeriesId, result);
        return result;
    }

    private async Task<TvMazeShowIds?> FetchShowIdsAsync(
        string seriesName,
        int? productionYear,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(TvMazeClient));

        var encodedName = Uri.EscapeDataString(seriesName);
        var url = new Uri($"{BaseUrl}/singlesearch/shows?q={encodedName}");

        try
        {
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "TVMaze lookup returned {StatusCode} for show '{SeriesName}'",
                        (int)response.StatusCode,
                        seriesName);
                }

                return null;
            }

            var show = await response.Content.ReadFromJsonAsync<TvMazeShow>(cancellationToken).ConfigureAwait(false);
            if (show is null)
            {
                return null;
            }

            // Validate the premiere year when we have one, to avoid accepting wrong-title matches.
            if (productionYear.HasValue
                && show.Premiered is { Length: >= 4 } premiered
                && int.TryParse(premiered.AsSpan(0, 4), out var premiereYear)
                && Math.Abs(premiereYear - productionYear.Value) > 1)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "TVMaze result for '{SeriesName}' premiered in {PremiereYear}, expected ~{ProductionYear} — skipping",
                        seriesName,
                        premiereYear,
                        productionYear.Value);
                }

                return null;
            }

            var tvdbId = show.Externals?.ThetvDb;
            var imdbId = show.Externals?.Imdb;

            if (tvdbId is null && imdbId is null)
            {
                return null;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "TVMaze resolved '{SeriesName}': tvdb={TvdbId}, imdb={ImdbId}",
                    seriesName,
                    tvdbId,
                    imdbId);
            }

            return new TvMazeShowIds(tvdbId, imdbId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Failed to look up '{SeriesName}' on TVMaze", seriesName);
            }

            return null;
        }
    }

    private sealed class TvMazeShow
    {
        [JsonPropertyName("premiered")]
        public string? Premiered { get; set; }

        [JsonPropertyName("externals")]
        public TvMazeExternals? Externals { get; set; }
    }

    private sealed class TvMazeExternals
    {
        [JsonPropertyName("thetvdb")]
        public int? ThetvDb { get; set; }

        [JsonPropertyName("imdb")]
        public string? Imdb { get; set; }
    }
}
