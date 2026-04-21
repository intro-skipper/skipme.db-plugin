// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Represents a single lookup item for the SkipMe.db <c>POST /v1/movies</c> endpoint.
/// </summary>
public class MovieLookupRequest
{
    /// <summary>Gets or sets the TMDB series or movie ID.</summary>
    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb series ID.</summary>
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the TVDB episode or movie ID.</summary>
    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; set; }

    /// <summary>Gets or sets the AniList series ID.</summary>
    [JsonPropertyName("anilist_id")]
    public int? AniListId { get; set; }

    /// <summary>Gets or sets the season number when looking up an episode.</summary>
    [JsonPropertyName("season")]
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode number when looking up an episode.</summary>
    [JsonPropertyName("episode")]
    public int? Episode { get; set; }

    /// <summary>Gets or sets the media duration in milliseconds.</summary>
    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }
}
