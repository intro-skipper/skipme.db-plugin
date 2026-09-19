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

/// <summary>Retrieves segment timestamps from SkipMe.db.</summary>
public class SkipMeApiClient
{
    private const int MaxRequestBytes = 100 * 1024 * 1024;
    // Keeps worst-case movie lookups within the worker's query limit.
    private const int MaxItemsPerRequest = 550;
    // Throttle batch starts to 500 ms apart.
    private static readonly TimeSpan MinimumBatchRequestInterval = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly SemaphoreSlim _requestPacingLock = new(1, 1);
    private static DateTimeOffset _lastRequestStartedUtc = DateTimeOffset.MinValue;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SkipMeApiClient> _logger;

    /// <summary>Initializes a new instance of the <see cref="SkipMeApiClient"/> class.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public SkipMeApiClient(IHttpClientFactory httpClientFactory, ILogger<SkipMeApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Fetches movie and episode segments in request order.</summary>
    /// <param name="requests">Lookup requests.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Responses aligned with <paramref name="requests"/>.</returns>
    public async Task<IReadOnlyList<MediaResponse?>> GetByMoviesBatchAsync(
        IReadOnlyList<MovieLookupRequest> requests,
        CancellationToken cancellationToken)
    {
        var result = await GetByMoviesBatchWithStatusAsync(requests, null, cancellationToken).ConfigureAwait(false);
        return result.Responses;
    }

    /// <summary>Fetches movie and episode segments with completion status.</summary>
    /// <param name="requests">Lookup requests.</param>
    /// <param name="onBatchCompleted">Optional callback after each completed batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Responses and whether every batch completed.</returns>
    internal Task<ApiBatchResult<MediaResponse>> GetByMoviesBatchWithStatusAsync(
        IReadOnlyList<MovieLookupRequest> requests,
        Action<int>? onBatchCompleted,
        CancellationToken cancellationToken)
    {
        return PostBatchAsync<MovieLookupRequest, MediaResponse>("/movies", requests, onBatchCompleted, cancellationToken);
    }

    /// <summary>Fetches show segments in request order.</summary>
    /// <param name="requests">Lookup requests.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Responses aligned with <paramref name="requests"/>.</returns>
    public async Task<IReadOnlyList<SeriesResponse?>> GetByShowsBatchAsync(
        IReadOnlyList<ShowLookupRequest> requests,
        CancellationToken cancellationToken)
    {
        var result = await GetByShowsBatchWithStatusAsync(requests, null, cancellationToken).ConfigureAwait(false);
        return result.Responses;
    }

    /// <summary>Fetches show segments with completion status.</summary>
    /// <param name="requests">Lookup requests.</param>
    /// <param name="onBatchCompleted">Optional callback after each completed batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Responses and whether every batch completed.</returns>
    internal Task<ApiBatchResult<SeriesResponse>> GetByShowsBatchWithStatusAsync(
        IReadOnlyList<ShowLookupRequest> requests,
        Action<int>? onBatchCompleted,
        CancellationToken cancellationToken)
    {
        return PostBatchAsync<ShowLookupRequest, SeriesResponse>("/shows", requests, onBatchCompleted, cancellationToken);
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
        var usageLimitExceeded = false;
        var client = _httpClientFactory.CreateClient(nameof(SkipMeApiClient));
        var url = new Uri($"{ApiConfiguration.Url.TrimEnd('/')}{endpointPath}");

        foreach (var itemBatch in ChunkItems(requests))
        {
            if (usageLimitExceeded)
            {
                results.AddRange(Enumerable.Repeat<TResponse?>(default, itemBatch.Count));
                completed = false;
                continue;
            }

            await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            var result = await PostBatchOnceAsync<TRequest, TResponse>(
                client,
                url,
                itemBatch,
                onBatchCompleted,
                cancellationToken).ConfigureAwait(false);
            completed &= result.Completed;
            usageLimitExceeded |= result.UsageLimitExceeded;
            results.AddRange(result.Responses);
        }

        return new ApiBatchResult<TResponse>(results, completed, usageLimitExceeded);
    }

    private async Task WaitForRequestSlotAsync(CancellationToken cancellationToken)
    {
        await _requestPacingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nextAllowedRequestUtc = _lastRequestStartedUtc + MinimumBatchRequestInterval;
            var delay = nextAllowedRequestUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestStartedUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _requestPacingLock.Release();
        }
    }

    private async Task<ApiBatchResult<TResponse>> PostBatchOnceAsync<TRequest, TResponse>(
        HttpClient client,
        Uri url,
        List<TRequest> itemBatch,
        Action<int>? onBatchCompleted,
        CancellationToken cancellationToken)
    {
        var endpoint = GetEndpointName(url);

        try
        {
            var result = await PostSingleBatchAsync<TRequest, TResponse>(client, url, itemBatch, cancellationToken).ConfigureAwait(false);
            if (result.Completed)
            {
                onBatchCompleted?.Invoke(itemBatch.Count);
            }

            return result;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return FailedBatch<TResponse>(itemBatch.Count);
            }

            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    ex,
                    "Timed out fetching {BatchCount} lookup item(s) from SkipMe.db API {Endpoint}; not retrying to avoid duplicating database work",
                    itemBatch.Count,
                    endpoint);
            }

            return FailedBatch<TResponse>(itemBatch.Count);
        }
        catch (HttpRequestException ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Failed to fetch {BatchCount} lookup item(s) from SkipMe.db API {Endpoint}", itemBatch.Count, endpoint);
            }

            return FailedBatch<TResponse>(itemBatch.Count);
        }
    }

    private async Task<ApiBatchResult<TResponse>> PostSingleBatchAsync<TRequest, TResponse>(
        HttpClient client,
        Uri url,
        List<TRequest> itemBatch,
        CancellationToken cancellationToken)
    {
        var endpoint = GetEndpointName(url);
        using var response = await client.PostAsJsonAsync(url, itemBatch, _jsonOptions, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var usageLimitExceeded = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || (response.StatusCode == System.Net.HttpStatusCode.InternalServerError
                    && LooksLikeUsageLimitResponse(responseBody));

            if (_logger.IsEnabled(LogLevel.Warning))
            {
                if (usageLimitExceeded)
                {
                    _logger.LogWarning(
                        "SkipMe.db API usage limit reached while fetching {BatchCount} item(s) from {Endpoint}: {ResponseBody}",
                        itemBatch.Count,
                        endpoint,
                        responseBody);
                }
                else
                {
                    _logger.LogWarning(
                        "SkipMe.db API returned {StatusCode} for {Endpoint} while fetching {BatchCount} item(s): {ResponseBody}",
                        (int)response.StatusCode,
                        endpoint,
                        itemBatch.Count,
                        responseBody);
                }
            }

            return new ApiBatchResult<TResponse>(
                Enumerable.Repeat<TResponse?>(default, itemBatch.Count).ToList(),
                false,
                usageLimitExceeded);
        }

        var payload = await response.Content.ReadFromJsonAsync<List<TResponse?>>(cancellationToken).ConfigureAwait(false) ?? [];
        if (payload.Count == itemBatch.Count)
        {
            // Preserve positions for no-result lookups.
            return new ApiBatchResult<TResponse>(payload, true);
        }

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "SkipMe.db API response count mismatch for {Endpoint}: expected {ExpectedCount}, got {ActualCount}",
                endpoint,
                itemBatch.Count,
                payload.Count);
        }

        var results = new List<TResponse?>(itemBatch.Count);
        for (var i = 0; i < itemBatch.Count; i++)
        {
            results.Add(i < payload.Count ? payload[i] : default);
        }

        return new ApiBatchResult<TResponse>(results, false);
    }

    private static string GetEndpointName(Uri url)
    {
        return url.Segments[^1].Trim('/');
    }

    private static bool LooksLikeUsageLimitResponse(string responseBody)
    {
        return responseBody.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
            || responseBody.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || responseBody.Contains("quota", StringComparison.OrdinalIgnoreCase);
    }

    private static ApiBatchResult<TResponse> FailedBatch<TResponse>(int count)
    {
        return new ApiBatchResult<TResponse>(Enumerable.Repeat<TResponse?>(default, count).ToList(), false);
    }

    private static IEnumerable<List<TRequest>> ChunkItems<TRequest>(IReadOnlyList<TRequest> requests)
    {
        var current = new List<TRequest>();
        var currentSize = 2;

        foreach (var request in requests)
        {
            var itemSize = JsonSerializer.SerializeToUtf8Bytes(request, _jsonOptions).Length;
            if (itemSize + 2 > MaxRequestBytes)
            {
                throw new InvalidOperationException("A single SkipMe.db batch item exceeds the 100MB request size limit.");
            }

            if (current.Count >= MaxItemsPerRequest)
            {
                yield return current;
                current = [];
                currentSize = 2;
            }

            var additional = itemSize + (current.Count > 0 ? 1 : 0);

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
