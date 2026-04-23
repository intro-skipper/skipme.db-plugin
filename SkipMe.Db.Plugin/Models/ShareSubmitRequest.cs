// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.ObjectModel;

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Request body for sharing enabled filtered items to SkipMe.db.
/// </summary>
public sealed class ShareSubmitRequest
{
    /// <summary>Gets the filtered series IDs currently visible in the UI.</summary>
    public Collection<string> FilteredSeriesIds { get; } = [];

    /// <summary>Gets the filtered movie IDs currently visible in the UI.</summary>
    public Collection<string> FilteredMovieIds { get; } = [];

    /// <summary>Gets the disabled series IDs from the current UI state.</summary>
    public Collection<string> DisabledSeriesIds { get; } = [];

    /// <summary>Gets the disabled season IDs from the current UI state.</summary>
    public Collection<string> DisabledSeasonIds { get; } = [];

    /// <summary>Gets the disabled movie IDs from the current UI state.</summary>
    public Collection<string> DisabledMovieIds { get; } = [];

    /// <summary>Gets the explicitly enabled specials season IDs from the current UI state.</summary>
    public Collection<string> EnabledSpecialsSeasonIds { get; } = [];
}
