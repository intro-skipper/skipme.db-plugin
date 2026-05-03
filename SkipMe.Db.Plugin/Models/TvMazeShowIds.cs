// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace SkipMe.Db.Plugin.Models;

/// <summary>
/// External IDs resolved by a TVMaze show lookup.
/// </summary>
/// <param name="TvdbId">The TheTVDB series ID, or <c>null</c> if not available.</param>
/// <param name="ImdbId">The IMDb show ID, or <c>null</c> if not available.</param>
public sealed record TvMazeShowIds(int? TvdbId, string? ImdbId);
