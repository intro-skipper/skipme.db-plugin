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
    /// Gets the set of Jellyfin series item IDs for which crowd-sourced segments are disabled.
    /// When a series ID is present, no segments will be surfaced for any episode in that series.
    /// </summary>
    public Collection<Guid> DisabledSeriesIds { get; } = [];

    /// <summary>
    /// Gets the set of Jellyfin season item IDs for which crowd-sourced segments are disabled.
    /// When a season ID is present, no segments will be surfaced for any episode in that season,
    /// unless the containing series is already disabled.
    /// </summary>
    public Collection<Guid> DisabledSeasonIds { get; } = [];

    /// <summary>
    /// Gets the set of Jellyfin movie item IDs for which crowd-sourced segments are disabled.
    /// When a movie ID is present, no segments will be surfaced for that movie.
    /// </summary>
    public Collection<Guid> DisabledMovieIds { get; } = [];

    /// <summary>
    /// Gets the set of Jellyfin season item IDs for season 0 (Specials) that have been explicitly enabled.
    /// Specials seasons are disabled by default; a season ID must be present here for its segments to be surfaced.
    /// </summary>
    public Collection<Guid> EnabledSpecialsSeasonIds { get; } = [];
}
