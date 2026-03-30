// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Represents the response from the SkipMe.db <c>GET /v1/media</c> endpoint.
/// </summary>
public class MediaResponse
{
    /// <summary>Gets or sets the TMDB series or movie ID.</summary>
    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the TVDB series or movie ID.</summary>
    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; set; }

    /// <summary>Gets or sets the AniList series ID.</summary>
    [JsonPropertyName("anilist_id")]
    public int? AniListId { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    [JsonPropertyName("season")]
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    [JsonPropertyName("episode")]
    public int? Episode { get; set; }

    /// <summary>Gets or sets the episode duration used for matching in milliseconds.</summary>
    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    /// <summary>Gets the intro segment timestamps.</summary>
    [JsonPropertyName("intro")]
    public IList<MediaTimestamp> Intro { get; init; } = [];

    /// <summary>Gets the recap segment timestamps.</summary>
    [JsonPropertyName("recap")]
    public IList<MediaTimestamp> Recap { get; init; } = [];

    /// <summary>Gets the credits segment timestamps.</summary>
    [JsonPropertyName("credits")]
    public IList<MediaTimestamp> Credits { get; init; } = [];

    /// <summary>Gets the preview segment timestamps.</summary>
    [JsonPropertyName("preview")]
    public IList<MediaTimestamp> Preview { get; init; } = [];
}
