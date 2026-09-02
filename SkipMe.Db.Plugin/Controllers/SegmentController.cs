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
    /// Gets synced and shareable segment counts grouped by series or movie.
    /// </summary>
    /// <returns>Segment counts for items that are shown on the plugin settings page.</returns>
    [HttpGet("Segments/Counts")]
    public ActionResult<SegmentCountResponse> GetSegmentCounts()
    {
        var seriesCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var movieCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shareableSeriesCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shareableMovieCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (itemId, count) in segmentStore.GetSegmentCountsByItemId())
        {
            switch (libraryManager.GetItemById(itemId))
            {
                case Movie movie:
                    movieCounts[movie.Id.ToString("N")] = count;
                    break;
                case Episode { Series: { } series }:
                    var seriesId = series.Id.ToString("N");
                    seriesCounts.TryGetValue(seriesId, out var existingCount);
                    seriesCounts[seriesId] = existingCount + count;
                    break;
            }
        }

        foreach (var (itemId, count) in shareSubmissionService.GetShareableSegmentCountsByItemId())
        {
            switch (libraryManager.GetItemById(itemId))
            {
                case Movie movie:
                    shareableMovieCounts[movie.Id.ToString("N")] = count;
                    break;
                case Episode { Series: { } series }:
                    var seriesId = series.Id.ToString("N");
                    shareableSeriesCounts.TryGetValue(seriesId, out var existingCount);
                    shareableSeriesCounts[seriesId] = existingCount + count;
                    break;
            }
        }

        return Ok(new SegmentCountResponse
        {
            Series = seriesCounts,
            Movies = movieCounts,
            ShareableSeries = shareableSeriesCounts,
            ShareableMovies = shareableMovieCounts,
        });
    }
}
