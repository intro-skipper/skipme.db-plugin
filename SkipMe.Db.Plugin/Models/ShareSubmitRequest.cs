// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// Request body for sharing enabled filtered items to SkipMe.db.
/// </summary>
public sealed class ShareSubmitRequest
{
    /// <summary>Gets the filtered series IDs currently visible in the UI.</summary>
    [JsonInclude]
    public IReadOnlyCollection<string> FilteredSeriesIds { get; init; } = [];

    /// <summary>Gets the filtered movie IDs currently visible in the UI.</summary>
    [JsonInclude]
    public IReadOnlyCollection<string> FilteredMovieIds { get; init; } = [];

    /// <summary>Gets the disabled series IDs from the current UI state.</summary>
    [JsonInclude]
    public IReadOnlyCollection<string> DisabledSeriesIds { get; init; } = [];

    /// <summary>Gets the disabled season IDs from the current UI state.</summary>
    [JsonInclude]
    public IReadOnlyCollection<string> DisabledSeasonIds { get; init; } = [];

    /// <summary>Gets the disabled movie IDs from the current UI state.</summary>
    [JsonInclude]
    public IReadOnlyCollection<string> DisabledMovieIds { get; init; } = [];

    /// <summary>Gets the explicitly enabled specials season IDs from the current UI state.</summary>
    [JsonInclude]
    public IReadOnlyCollection<string> EnabledSpecialsSeasonIds { get; init; } = [];
}
