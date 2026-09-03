// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net.Mime;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkipMe.Db.Plugin.Models;
using SkipMe.Db.Plugin.Services;

namespace SkipMe.Db.Plugin.Controllers;

/// <summary>
/// Read-only API for locally synced and shareable segments.
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("SkipMeDb")]
public sealed class SegmentController(
    SegmentStore segmentStore,
    ShareSubmissionService shareSubmissionService,
    ILibraryManager libraryManager) : ControllerBase
{
    /// <summary>
    /// Gets synced segment counts grouped by series or movie.
    /// </summary>
    /// <returns>Segment counts for items that are shown on the plugin settings page.</returns>
    [HttpGet("Segments/Counts")]
    public ActionResult<SegmentCountResponse> GetSegmentCounts()
    {
        return Ok(MapCounts(segmentStore.GetSegmentCountsByItemId()));
    }

    /// <summary>
    /// Gets unshared Intro Skipper segment counts grouped by series or movie.
    /// </summary>
    /// <returns>Shareable segment counts for items shown on the plugin settings page.</returns>
    [HttpGet("Share/Counts")]
    public ActionResult<SegmentCountResponse> GetShareableSegmentCounts()
    {
        return Ok(MapCounts(shareSubmissionService.GetShareableSegmentCountsByItemId()));
    }

    private SegmentCountResponse MapCounts(IReadOnlyDictionary<Guid, int> itemCounts)
    {
        var seriesCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var movieCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (itemId, count) in itemCounts)
        {
            switch (libraryManager.GetItemById(itemId))
            {
                case Movie movie:
                    movieCounts[movie.Id.ToString("N")] = count;
                    break;
                case Episode episode:
                    // SeriesId is persisted on the episode and is available even when
                    // the navigation property has not been hydrated by the library
                    // manager. Falling back to Series keeps compatibility with items
                    // created by older Jellyfin versions.
                    var seriesId = episode.SeriesId;
                    if (seriesId == Guid.Empty && episode.Series is { } series)
                    {
                        seriesId = series.Id;
                    }

                    if (seriesId != Guid.Empty)
                    {
                        var seriesKey = seriesId.ToString("N");
                        seriesCounts.TryGetValue(seriesKey, out var existingCount);
                        seriesCounts[seriesKey] = existingCount + count;
                    }

                    break;
            }
        }

        return new SegmentCountResponse
        {
            Series = seriesCounts,
            Movies = movieCounts,
        };
    }
}
