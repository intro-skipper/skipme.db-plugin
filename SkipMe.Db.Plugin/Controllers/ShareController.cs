// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
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
public sealed class ShareController(ShareSubmissionService shareSubmissionService) : ControllerBase
{
    private readonly ShareSubmissionService _shareSubmissionService = shareSubmissionService;

    /// <summary>
    /// Shares enabled filtered items.
    /// </summary>
    /// <param name="request">Share request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The share run summary.</returns>
    [HttpPost("Share")]
    public async Task<ActionResult<ShareSubmitResponse>> ShareAsync(
        [FromBody] ShareSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _shareSubmissionService.ShareAsync(request, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
