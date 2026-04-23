// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Result of a share submission run.
/// </summary>
public sealed class ShareSubmitResponse
{
    /// <summary>Gets or sets a value indicating whether at least one submission request succeeded.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets the total number of segments accepted by SkipMe.db.</summary>
    public int SharedSegments { get; set; }

    /// <summary>Gets or sets the number of show season requests sent.</summary>
    public int SharedShowSeasons { get; set; }

    /// <summary>Gets or sets the number of movie segment requests sent.</summary>
    public int SharedMovies { get; set; }

    /// <summary>Gets or sets the number of candidate segments skipped due to local dedupe history.</summary>
    public int SkippedAlreadyShared { get; set; }

    /// <summary>Gets or sets the number of candidate segments skipped due to missing IDs or metadata.</summary>
    public int SkippedMissingMetadata { get; set; }

    /// <summary>Gets or sets the number of candidate items skipped because no Intro Skipper timestamp exists.</summary>
    public int SkippedNoSegments { get; set; }

    /// <summary>Gets or sets optional diagnostic details for failed requests.</summary>
    public string? Error { get; set; }
}
