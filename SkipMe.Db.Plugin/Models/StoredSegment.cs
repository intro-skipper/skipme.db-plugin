// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// A lightweight segment record persisted in the local segment database.
/// </summary>
public class StoredSegment
{
    /// <summary>Gets or sets the segment type (e.g. "intro", "credits", "recap", "preview", "commercial").</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the segment start time in milliseconds.</summary>
    public long StartMs { get; set; }

    /// <summary>Gets or sets the segment end time in milliseconds.</summary>
    public long EndMs { get; set; }
}
