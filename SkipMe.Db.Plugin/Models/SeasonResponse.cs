// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Represents the response from the SkipMe.db API for a season's segments.
/// </summary>
public class SeasonResponse
{
    /// <summary>Gets or sets the TVDB season ID used to query this season.</summary>
    [JsonPropertyName("tvdb_season_id")]
    public int? TvdbSeasonId { get; set; }

    /// <summary>Gets or sets the TMDB series ID used to query this season.</summary>
    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the AniList series ID used to query this season.</summary>
    [JsonPropertyName("anilist_id")]
    public int? AniListId { get; set; }

    /// <summary>Gets or sets the season number used to query this season.</summary>
    [JsonPropertyName("season")]
    public int? Season { get; set; }

    /// <summary>Gets the list of segment entries for all episodes in this season.</summary>
    [JsonPropertyName("segments")]
    public IList<SegmentEntry> Segments { get; } = [];
}
