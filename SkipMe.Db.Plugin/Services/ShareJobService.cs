// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkipMe.Db.Plugin.Models;

namespace SkipMe.Db.Plugin.Services;

/// <summary>Runs share submissions independently of the browser request lifetime.</summary>
public sealed class ShareJobService
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly ShareSubmissionService _submissionService;
    private readonly ILogger<ShareJobService> _logger;
    private readonly ConcurrentDictionary<Guid, ShareJob> _jobs = new();

    /// <summary>Initializes a new instance of the <see cref="ShareJobService"/> class.</summary>
    /// <param name="submissionService">Share submission service.</param>
    /// <param name="logger">Logger.</param>
    public ShareJobService(ShareSubmissionService submissionService, ILogger<ShareJobService> logger)
    {
        _submissionService = submissionService;
        _logger = logger;
    }

    /// <summary>Queues a share request and returns immediately.</summary>
    /// <param name="request">Share request.</param>
    /// <returns>The new job identifier.</returns>
    public Guid Enqueue(ShareSubmitRequest request)
    {
        RemoveExpiredJobs();

        var job = new ShareJob(Guid.NewGuid());
        _jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job, request));
        return job.Id;
    }

    /// <summary>Gets a job snapshot.</summary>
    /// <param name="jobId">Job identifier.</param>
    /// <param name="response">The job response.</param>
    /// <returns><see langword="true"/> when the job exists.</returns>
    public bool TryGet(Guid jobId, out ShareSubmitResponse response)
    {
        RemoveExpiredJobs();

        if (_jobs.TryGetValue(jobId, out var job))
        {
            response = job.GetResponse();
            return true;
        }

        response = new ShareSubmitResponse();
        return false;
    }

    private async Task RunAsync(ShareJob job, ShareSubmitRequest request)
    {
        job.SetResponse(new ShareSubmitResponse
        {
            Completed = false,
            JobId = job.Id,
            Status = "running",
            Ok = true,
        });

        try
        {
            // This is intentionally independent from the controller request token.
            // The browser is only polling this job and must not cancel work when its
            // original POST connection closes.
            var result = await _submissionService.ShareAsync(request, CancellationToken.None).ConfigureAwait(false);
            job.SetResponse(new ShareSubmitResponse
            {
                Completed = true,
                JobId = job.Id,
                Status = result.Ok ? "completed" : "failed",
                Ok = result.Ok,
                SharedSegments = result.SharedSegments,
                SharedShowSeasons = result.SharedShowSeasons,
                SharedMovies = result.SharedMovies,
                SkippedAlreadyShared = result.SkippedAlreadyShared,
                SkippedMissingMetadata = result.SkippedMissingMetadata,
                SkippedNoSegments = result.SkippedNoSegments,
                Error = result.Error,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Share job {JobId} failed", job.Id);
            job.SetResponse(new ShareSubmitResponse
            {
                Completed = true,
                JobId = job.Id,
                Status = "failed",
                Error = ex.Message,
            });
        }
        finally
        {
            job.MarkCompleted();
        }
    }

    private void RemoveExpiredJobs()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var pair in _jobs)
        {
            if (pair.Value.CompletedAt is { } completedAt && completedAt < cutoff)
            {
                _jobs.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class ShareJob(Guid id)
    {
        private readonly object _sync = new();
        private ShareSubmitResponse _response = new()
        {
            Completed = false,
            JobId = id,
            Status = "queued",
            Ok = true,
        };

        public Guid Id { get; } = id;

        public DateTimeOffset? CompletedAt { get; private set; }

        public ShareSubmitResponse GetResponse()
        {
            lock (_sync)
            {
                return _response;
            }
        }

        public void SetResponse(ShareSubmitResponse response)
        {
            lock (_sync)
            {
                _response = response;
            }
        }

        public void MarkCompleted()
        {
            CompletedAt = DateTimeOffset.UtcNow;
        }
    }
}
