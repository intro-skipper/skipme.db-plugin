// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Counts of locally synced segments grouped by their settings-page item.
/// </summary>
public sealed class SegmentCountResponse
{
    /// <summary>Gets the segment counts for series, including all of each series' episodes.</summary>
    public Dictionary<string, int> Series { get; init; } = [];

    /// <summary>Gets the segment counts for movies.</summary>
    public Dictionary<string, int> Movies { get; init; } = [];
}
