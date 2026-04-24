// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkipMe.Db.Plugin.Models;

namespace SkipMe.Db.Plugin.Services;

/// <summary>
/// HTTP client for the SkipMe.db API at https://db.skipme.workers.dev.
/// Fetches crowd-sourced segment timestamps for TV series and movies.
/// </summary>
public class SkipMeApiClient
{
    private const string BaseUrl = "https://db.skipme.workers.dev";
    private const int MaxRequestBytes = 100 * 1024 * 1024;

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
    /// Fetches segment timestamps for many movie/episode lookups via <c>POST /v1/movies</c>.
    /// </summary>
    /// <param name="requests">The lookup requests.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A response list that aligns with the request order.</returns>
    public Task<IReadOnlyList<MediaResponse?>> GetByMoviesBatchAsync(
        IReadOnlyList<MovieLookupRequest> requests,
        CancellationToken cancellationToken)
    {
        return PostBatchAsync<MovieLookupRequest, MediaResponse>("/v1/movies", requests, cancellationToken);
    }

    /// <summary>
    /// Fetches segment timestamps for many show lookups via <c>POST /v1/shows</c>.
    /// </summary>
    /// <param name="requests">The lookup requests.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A response list that aligns with the request order.</returns>
    public Task<IReadOnlyList<SeriesResponse?>> GetByShowsBatchAsync(
        IReadOnlyList<ShowLookupRequest> requests,
        CancellationToken cancellationToken)
    {
        return PostBatchAsync<ShowLookupRequest, SeriesResponse>("/v1/shows", requests, cancellationToken);
    }

    private async Task<IReadOnlyList<TResponse?>> PostBatchAsync<TRequest, TResponse>(
        string endpointPath,
        IReadOnlyList<TRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return [];
        }

        var results = new List<TResponse?>(requests.Count);
        var client = _httpClientFactory.CreateClient(nameof(SkipMeApiClient));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SkipMe.db", "0.0"));
        var url = new Uri($"{BaseUrl}{endpointPath}");

        foreach (var batch in ChunkByMaxRequestSize(requests))
        {
            try
            {
                using var response = await client.PostAsJsonAsync(url, batch, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("No results found from SkipMe.db API at {Url}", url);
                    results.AddRange(Enumerable.Repeat<TResponse?>(default, batch.Count));
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "SkipMe.db API returned {StatusCode} for {Url}",
                        (int)response.StatusCode,
                        url);
                    results.AddRange(Enumerable.Repeat<TResponse?>(default, batch.Count));
                    continue;
                }

                var payload = await response.Content.ReadFromJsonAsync<List<TResponse?>>(cancellationToken).ConfigureAwait(false) ?? [];
                if (payload.Count == batch.Count)
                {
                    results.AddRange(payload);
                    continue;
                }

                _logger.LogWarning(
                    "SkipMe.db API response count mismatch for {Url}: expected {ExpectedCount}, got {ActualCount}",
                    url,
                    batch.Count,
                    payload.Count);

                for (var i = 0; i < batch.Count; i++)
                {
                    results.Add(i < payload.Count ? payload[i] : default);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return results;
                }

                _logger.LogWarning(ex, "Failed to fetch segments from SkipMe.db API at {Url}", url);
                results.AddRange(Enumerable.Repeat<TResponse?>(default, batch.Count));
            }
        }

        return results;
    }

    private static IEnumerable<List<TRequest>> ChunkByMaxRequestSize<TRequest>(IReadOnlyList<TRequest> requests)
    {
        var current = new List<TRequest>();
        var currentSize = 2; // []

        foreach (var request in requests)
        {
            JsonSerializerOptions options = new()
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            var itemSize = JsonSerializer.SerializeToUtf8Bytes(request, options).Length;
            if (itemSize + 2 > MaxRequestBytes)
            {
                throw new InvalidOperationException("A single SkipMe.db batch item exceeds the 100MB request size limit.");
            }

            var additional = itemSize + (current.Count > 0 ? 1 : 0); // item + comma

            if (current.Count > 0 && currentSize + additional > MaxRequestBytes)
            {
                yield return current;
                current = [];
                currentSize = 2;
            }

            current.Add(request);
            currentSize += itemSize + (current.Count > 1 ? 1 : 0);
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }
}
