// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Timestamp used to deduplicate previously shared timestamps.
/// </summary>
/// <param name="ItemId">Jellyfin item ID (movie or episode).</param>
/// <param name="Segment">Segment type.</param>
/// <param name="StartMs">Segment start in milliseconds.</param>
/// <param name="EndMs">Segment end in milliseconds.</param>
/// <param name="DurationMs">Item duration in milliseconds.</param>
public sealed record SharedUploadTimestamp(
    Guid ItemId,
    string Segment,
    long StartMs,
    long EndMs,
    long DurationMs);
