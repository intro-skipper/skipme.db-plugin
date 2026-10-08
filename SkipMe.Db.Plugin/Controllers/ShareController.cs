// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Net.Mime;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkipMe.Db.Plugin.Models;
using SkipMe.Db.Plugin.Services;

namespace SkipMe.Db.Plugin.Controllers;

/// <summary>
/// Share API for uploading Intro Skipper timestamps to SkipMe.db.
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("SkipMeDb")]
public sealed class ShareController(ShareJobService shareJobService) : ControllerBase
{
    /// <summary>
    /// Shares enabled filtered items.
    /// </summary>
    /// <param name="request">Share request payload.</param>
    /// <returns>The share run summary.</returns>
    [HttpPost("Share")]
    public ActionResult<ShareSubmitResponse> ShareAsync([FromBody] ShareSubmitRequest request)
    {
        var jobId = shareJobService.Enqueue(request);
        return Accepted(new ShareSubmitResponse
        {
            Completed = false,
            JobId = jobId,
            Status = "queued",
            Ok = true,
        });
    }

    /// <summary>
    /// Gets the status of an asynchronous share job.
    /// </summary>
    /// <param name="jobId">Share job identifier.</param>
    /// <returns>The current share job status.</returns>
    [HttpGet("Share/{jobId:guid}")]
    public ActionResult<ShareSubmitResponse> GetShareStatus(Guid jobId)
    {
        if (!shareJobService.TryGet(jobId, out var response))
        {
            return NotFound();
        }

        return response.Completed ? Ok(response) : Accepted(response);
    }
}
