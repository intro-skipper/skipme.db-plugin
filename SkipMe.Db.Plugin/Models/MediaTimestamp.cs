// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Represents a single timestamp entry within a <see cref="MediaResponse"/>.
/// </summary>
public class MediaTimestamp
{
    /// <summary>Gets or sets the segment start time in milliseconds.</summary>
    [JsonPropertyName("start_ms")]
    public long StartMs { get; set; }

    /// <summary>Gets or sets the segment end time in milliseconds, or <c>null</c> if open-ended.</summary>
    [JsonPropertyName("end_ms")]
    public long? EndMs { get; set; }

    /// <summary>Gets or sets the episode duration used for this result in milliseconds.</summary>
    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    /// <summary>Gets or sets the number of user submissions averaged to produce this entry.</summary>
    [JsonPropertyName("submissions")]
    public int Submissions { get; set; }
}
