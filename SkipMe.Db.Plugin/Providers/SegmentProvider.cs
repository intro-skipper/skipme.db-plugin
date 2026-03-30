// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
using SkipMe.Db.Plugin.Models;
using SkipMe.Db.Plugin.Services;

namespace SkipMe.Db.Plugin.Providers;

/// <summary>
/// Provides media segment timestamps for movies and TV episodes by reading from the local
/// SkipMe.db segment store populated by <see cref="Tasks.SyncSegmentsTask"/>.
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

    private readonly SegmentStore _segmentStore;
    private readonly ILogger<SegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentProvider"/> class.
    /// </summary>
    /// <param name="segmentStore">The local segment store.</param>
    /// <param name="logger">The logger.</param>
    public SegmentProvider(SegmentStore segmentStore, ILogger<SegmentProvider> logger)
    {
        _segmentStore = segmentStore;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => Plugin.Instance!.Name;

    /// <inheritdoc/>
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode or Movie);

    /// <inheritdoc/>
    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var storedSegments = _segmentStore.GetSegments(request.ItemId);
        if (storedSegments is null)
        {
            _logger.LogDebug(
                "No segments in local store for item {ItemId} — run the sync task to populate",
                request.ItemId);
            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>([]);
        }

        return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(BuildSegments(request.ItemId, storedSegments));
    }

    private List<MediaSegmentDto> BuildSegments(Guid itemId, IReadOnlyList<StoredSegment> storedSegments)
    {
        var segments = new List<MediaSegmentDto>();

        foreach (var entry in storedSegments)
        {
            if (!SegmentTypeMappings.TryGetValue(entry.Type, out var segmentType))
            {
                _logger.LogDebug("Unknown segment type '{SegmentType}' — skipping", entry.Type);
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
