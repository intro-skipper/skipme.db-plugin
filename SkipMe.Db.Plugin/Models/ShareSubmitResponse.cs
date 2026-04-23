// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Result of a share submission run.
/// </summary>
public sealed class ShareSubmitResponse
{
    /// <summary>Gets a value indicating whether at least one submission request succeeded.</summary>
    public bool Ok { get; init; }

    /// <summary>Gets the total number of segments accepted by SkipMe.db.</summary>
    public int SharedSegments { get; init; }

    /// <summary>Gets the number of show season requests sent.</summary>
    public int SharedShowSeasons { get; init; }

    /// <summary>Gets the number of movie segment requests sent.</summary>
    public int SharedMovies { get; init; }

    /// <summary>Gets the number of candidate segments skipped due to local dedupe history.</summary>
    public int SkippedAlreadyShared { get; init; }

    /// <summary>Gets the number of candidate segments skipped due to missing IDs or metadata.</summary>
    public int SkippedMissingMetadata { get; init; }

    /// <summary>Gets the number of candidate items skipped because no Intro Skipper timestamp exists.</summary>
    public int SkippedNoSegments { get; init; }

    /// <summary>Gets optional diagnostic details for failed requests.</summary>
    public string? Error { get; init; }
}
