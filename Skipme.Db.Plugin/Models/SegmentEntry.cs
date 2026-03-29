// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace Skipme.Db.Plugin.Models;

/// <summary>
/// Represents a single segment entry returned by the Skipme.DB API.
/// </summary>
public class SegmentEntry
{
    /// <summary>Gets or sets the TVDB episode ID.</summary>
    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; set; }

    /// <summary>Gets or sets the TVDB season ID.</summary>
    [JsonPropertyName("tvdb_season_id")]
    public int? TvdbSeasonId { get; set; }

    /// <summary>Gets or sets the TMDB series ID.</summary>
    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the AniList series ID.</summary>
    [JsonPropertyName("anilist_id")]
    public int? AniListId { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    [JsonPropertyName("season")]
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    [JsonPropertyName("episode")]
    public int Episode { get; set; }

    /// <summary>Gets or sets the segment type (e.g. "intro", "credits", "recap", "preview", "commercial").</summary>
    [JsonPropertyName("segment")]
    public string Segment { get; set; } = string.Empty;

    /// <summary>Gets or sets the segment start time in milliseconds.</summary>
    [JsonPropertyName("start_ms")]
    public long StartMs { get; set; }

    /// <summary>Gets or sets the segment end time in milliseconds.</summary>
    [JsonPropertyName("end_ms")]
    public long EndMs { get; set; }

    /// <summary>Gets or sets the total episode duration in milliseconds.</summary>
    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    /// <summary>Gets or sets the number of user submissions that contributed to this entry.</summary>
    [JsonPropertyName("submissions")]
    public int Submissions { get; set; }
}
