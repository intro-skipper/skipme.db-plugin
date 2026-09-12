// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const int MaxBatchSegments = 1000;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
    public async Task<IReadOnlyList<MediaResponse?>> GetByMoviesBatchAsync(
        IReadOnlyList<MovieLookupRequest> requests,
        CancellationToken cancellationToken)
    {
        var result = await GetByMoviesBatchWithStatusAsync(requests, null, cancellationToken).ConfigureAwait(false);
        return result.Responses;
    }

    /// <summary>
    /// Fetches segment timestamps for many movie/episode lookups via <c>POST /v1/movies</c>.
    /// </summary>
    /// <param name="requests">The lookup requests.</param>
    /// <param name="onBatchCompleted">Optional callback invoked after each request batch finishes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A response list plus whether all batches completed reliably.</returns>
    internal Task<ApiBatchResult<MediaResponse>> GetByMoviesBatchWithStatusAsync(
        IReadOnlyList<MovieLookupRequest> requests,
        Action<int>? onBatchCompleted,
        CancellationToken cancellationToken)
    {
        return PostBatchAsync<MovieLookupRequest, MediaResponse>("/v1/movies", requests, onBatchCompleted, cancellationToken);
    }

    /// <summary>
    /// Fetches segment timestamps for many show lookups via <c>POST /v1/shows</c>.
    /// </summary>
    /// <param name="requests">The lookup requests.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A response list that aligns with the request order.</returns>
    public async Task<IReadOnlyList<SeriesResponse?>> GetByShowsBatchAsync(
        IReadOnlyList<ShowLookupRequest> requests,
        CancellationToken cancellationToken)
    {
        var result = await GetByShowsBatchWithStatusAsync(requests, null, cancellationToken).ConfigureAwait(false);
        return result.Responses;
    }

    /// <summary>
    /// Fetches segment timestamps for many show lookups via <c>POST /v1/shows</c>.
    /// </summary>
    /// <param name="requests">The lookup requests.</param>
    /// <param name="onBatchCompleted">Optional callback invoked after each request batch finishes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A response list plus whether all batches completed reliably.</returns>
    internal Task<ApiBatchResult<SeriesResponse>> GetByShowsBatchWithStatusAsync(
        IReadOnlyList<ShowLookupRequest> requests,
        Action<int>? onBatchCompleted,
        CancellationToken cancellationToken)
    {
        return PostBatchAsync<ShowLookupRequest, SeriesResponse>("/v1/shows", requests, onBatchCompleted, cancellationToken);
    }

    private async Task<ApiBatchResult<TResponse>> PostBatchAsync<TRequest, TResponse>(
        string endpointPath,
        IReadOnlyList<TRequest> requests,
        Action<int>? onBatchCompleted,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return new ApiBatchResult<TResponse>([], true);
        }

        var results = new List<TResponse?>(requests.Count);
        var completed = true;
        var client = _httpClientFactory.CreateClient(nameof(SkipMeApiClient));
        var url = new Uri($"{BaseUrl}{endpointPath}");

        foreach (var batch in ChunkRequests(requests))
        {
            var result = await PostBatchWithFallbackAsync<TRequest, TResponse>(
                client,
                url,
                batch,
                onBatchCompleted,
                cancellationToken).ConfigureAwait(false);
            completed &= result.Completed;
            results.AddRange(result.Responses);
        }

        return new ApiBatchResult<TResponse>(results, completed);
    }

    private async Task<ApiBatchResult<TResponse>> PostBatchWithFallbackAsync<TRequest, TResponse>(
        HttpClient client,
        Uri url,
        IReadOnlyList<TRequest> batch,
        Action<int>? onBatchCompleted,
        CancellationToken cancellationToken)
    {
        var endpoint = GetEndpointName(url);

        try
        {
            var result = await PostSingleBatchAsync<TRequest, TResponse>(client, url, batch, cancellationToken).ConfigureAwait(false);
            onBatchCompleted?.Invoke(batch.Count);
            return result;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return FailedBatch<TResponse>(batch.Count);
            }

            if (batch.Count <= 1)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Failed to fetch {BatchCount} segment lookup(s) from SkipMe.db API {Endpoint}", batch.Count, endpoint);
                }

                return FailedBatch<TResponse>(batch.Count);
            }

            var midpoint = batch.Count / 2;
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    ex,
                    "Timed out fetching {BatchCount} segment lookup(s) from SkipMe.db API {Endpoint}; retrying as {FirstBatchCount} and {SecondBatchCount} lookup batch(es)",
                    batch.Count,
                    endpoint,
                    midpoint,
                    batch.Count - midpoint);
            }

            var first = await PostBatchWithFallbackAsync<TRequest, TResponse>(
                client,
                url,
                batch.Take(midpoint).ToList(),
                onBatchCompleted,
                cancellationToken).ConfigureAwait(false);
            var second = await PostBatchWithFallbackAsync<TRequest, TResponse>(
                client,
                url,
                batch.Skip(midpoint).ToList(),
                onBatchCompleted,
                cancellationToken).ConfigureAwait(false);

            return new ApiBatchResult<TResponse>(
                first.Responses.Concat(second.Responses).ToList(),
                first.Completed && second.Completed);
        }
        catch (HttpRequestException ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Failed to fetch {BatchCount} segment lookup(s) from SkipMe.db API {Endpoint}", batch.Count, endpoint);
            }

            return FailedBatch<TResponse>(batch.Count);
        }
    }

    private async Task<ApiBatchResult<TResponse>> PostSingleBatchAsync<TRequest, TResponse>(
        HttpClient client,
        Uri url,
        IReadOnlyList<TRequest> batch,
        CancellationToken cancellationToken)
    {
        var endpoint = GetEndpointName(url);
        using var response = await client.PostAsJsonAsync(url, batch, _jsonOptions, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "SkipMe.db API returned {StatusCode} for {Endpoint} while fetching {BatchCount} item(s)",
                    (int)response.StatusCode,
                    endpoint,
                    batch.Count);
            }

            return FailedBatch<TResponse>(batch.Count);
        }

        var payload = await response.Content.ReadFromJsonAsync<List<TResponse?>>(cancellationToken).ConfigureAwait(false) ?? [];
        if (payload.Count == batch.Count)
        {
            // Null entries represent valid no-result lookups and retain their position in the batch.
            return new ApiBatchResult<TResponse>(payload, true);
        }

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "SkipMe.db API response count mismatch for {Endpoint}: expected {ExpectedCount}, got {ActualCount}",
                endpoint,
                batch.Count,
                payload.Count);
        }

        var results = new List<TResponse?>(batch.Count);
        for (var i = 0; i < batch.Count; i++)
        {
            results.Add(i < payload.Count ? payload[i] : default);
        }

        return new ApiBatchResult<TResponse>(results, false);
    }

    private static string GetEndpointName(Uri url)
    {
        return url.Segments[^1].Trim('/');
    }

    private static ApiBatchResult<TResponse> FailedBatch<TResponse>(int count)
    {
        return new ApiBatchResult<TResponse>(Enumerable.Repeat<TResponse?>(default, count).ToList(), false);
    }

    private static IEnumerable<List<TRequest>> ChunkRequests<TRequest>(IReadOnlyList<TRequest> requests)
    {
        var current = new List<TRequest>();
        var currentSize = 2; // []

        foreach (var request in requests)
        {
            var itemSize = JsonSerializer.SerializeToUtf8Bytes(request, _jsonOptions).Length;
            if (itemSize + 2 > MaxRequestBytes)
            {
                throw new InvalidOperationException("A single SkipMe.db batch item exceeds the 100MB request size limit.");
            }

            if (current.Count >= MaxBatchSegments)
            {
                yield return current;
                current = [];
                currentSize = 2;
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
