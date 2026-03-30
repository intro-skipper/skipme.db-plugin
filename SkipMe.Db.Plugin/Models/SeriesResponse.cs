// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Represents the response from the SkipMe.db API for a series' segments.
/// </summary>
public class SeriesResponse
{
    /// <summary>Gets or sets the TVDB series ID used to query this series.</summary>
    [JsonPropertyName("tvdb_series_id")]
    public int? TvdbSeriesId { get; set; }

    /// <summary>Gets or sets the TMDB series ID used to query this series.</summary>
    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the AniList series ID used to query this series.</summary>
    [JsonPropertyName("anilist_id")]
    public int? AniListId { get; set; }

    /// <summary>Gets the list of segment entries for all episodes across all seasons in this series.</summary>
    [JsonPropertyName("segments")]
    public IList<SegmentEntry> Segments { get; init; } = [];
}
