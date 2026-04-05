// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

namespace SkipMe.Db.Plugin.Configuration;

/// <summary>
/// Plugin configuration for SkipMe.db.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the set of Jellyfin series item IDs for which crowd-sourced segments are disabled.
    /// When a series ID is present, no segments will be surfaced for any episode in that series.
    /// </summary>
    public Collection<Guid> DisabledSeriesIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the set of Jellyfin season item IDs for which crowd-sourced segments are disabled.
    /// When a season ID is present, no segments will be surfaced for any episode in that season,
    /// unless the containing series is already disabled.
    /// </summary>
    public Collection<Guid> DisabledSeasonIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the set of Jellyfin movie item IDs for which crowd-sourced segments are disabled.
    /// When a movie ID is present, no segments will be surfaced for that movie.
    /// </summary>
    public Collection<Guid> DisabledMovieIds { get; set; } = [];
}
