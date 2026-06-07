// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;

namespace SkipMe.Db.Plugin.Services;

/// <summary>
/// Result of a batch API lookup.
/// </summary>
/// <typeparam name="TResponse">Response item type.</typeparam>
internal sealed record ApiBatchResult<TResponse>(IReadOnlyList<TResponse?> Responses, bool Completed);
