// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Represents a single lookup item for the SkipMe.db <c>POST /v1/shows</c> endpoint.
/// </summary>
public class ShowLookupRequest
{
    /// <summary>Gets or sets the TVDB series ID.</summary>
    [JsonPropertyName("tvdb_series_id")]
    public int? TvdbSeriesId { get; set; }

    /// <summary>Gets or sets the TMDB series ID.</summary>
    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb series ID.</summary>
    [JsonPropertyName("imdb_series_id")]
    public string? ImdbSeriesId { get; set; }

    /// <summary>Gets or sets the AniList series ID.</summary>
    [JsonPropertyName("anilist_id")]
    public int? AniListId { get; set; }
}
