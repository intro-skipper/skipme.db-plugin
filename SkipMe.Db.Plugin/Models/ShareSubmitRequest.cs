// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Request body for sharing enabled filtered items to SkipMe.db.
/// </summary>
public sealed class ShareSubmitRequest
{
    /// <summary>Gets or sets the filtered series IDs currently visible in the UI.</summary>
    public IList<string> FilteredSeriesIds { get; set; } = [];

    /// <summary>Gets or sets the filtered movie IDs currently visible in the UI.</summary>
    public IList<string> FilteredMovieIds { get; set; } = [];

    /// <summary>Gets or sets disabled series IDs from the current UI state.</summary>
    public IList<string> DisabledSeriesIds { get; set; } = [];

    /// <summary>Gets or sets disabled season IDs from the current UI state.</summary>
    public IList<string> DisabledSeasonIds { get; set; } = [];

    /// <summary>Gets or sets disabled movie IDs from the current UI state.</summary>
    public IList<string> DisabledMovieIds { get; set; } = [];

    /// <summary>Gets or sets explicitly enabled specials season IDs from the current UI state.</summary>
    public IList<string> EnabledSpecialsSeasonIds { get; set; } = [];
}
