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
using MediaBrowser.Controller.Library;
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
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentProvider"/> class.
    /// </summary>
    /// <param name="segmentStore">The local segment store.</param>
    /// <param name="libraryManager">The Jellyfin library manager, used to resolve items for per-series/season checks.</param>
    /// <param name="logger">The logger.</param>
    public SegmentProvider(SegmentStore segmentStore, ILibraryManager libraryManager, ILogger<SegmentProvider> logger)
    {
        _segmentStore = segmentStore;
        _libraryManager = libraryManager;
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

        if (IsItemDisabled(request.ItemId))
        {
            _logger.LogDebug(
                "Segments suppressed for item {ItemId} — series or season is disabled in configuration",
                request.ItemId);
            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>([]);
        }

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

    /// <summary>
    /// Returns <c>true</c> when the item's series or season has been disabled in the plugin configuration,
    /// meaning no segments should be surfaced for it regardless of what is stored locally.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID to check.</param>
    private bool IsItemDisabled(Guid itemId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return false;
        }

        // Fast path: nothing is disabled — avoid any library lookup.
        if (config.DisabledSeriesIds.Count == 0 && config.DisabledSeasonIds.Count == 0)
        {
            return false;
        }

        if (_libraryManager.GetItemById(itemId) is not Episode episode)
        {
            return false;
        }

        // Series-level check.
        if (episode.Series is { } series && config.DisabledSeriesIds.Contains(series.Id))
        {
            return true;
        }

        // Season-level check (ParentId is the season's item ID for an episode).
        if (episode.ParentId != Guid.Empty && config.DisabledSeasonIds.Contains(episode.ParentId))
        {
            return true;
        }

        return false;
    }
}
