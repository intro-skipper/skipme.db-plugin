// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using SkipMe.Db.Plugin.Models;

namespace SkipMe.Db.Plugin.Services;

/// <summary>
/// Local JSON-backed store for crowd-sourced media segments retrieved from the SkipMe.db API.
/// Segment lists are keyed by Jellyfin item ID and persisted to a file in the data directory.
/// </summary>
public sealed class SegmentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _dbPath;
    private readonly ILogger<SegmentStore> _logger;
    private readonly object _syncRoot = new();

    private Dictionary<Guid, List<StoredSegment>> _segments = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentStore"/> class.
    /// Loads any previously persisted data from disk.
    /// </summary>
    /// <param name="appPaths">Application paths used to locate the data directory.</param>
    /// <param name="logger">The logger.</param>
    public SegmentStore(IApplicationPaths appPaths, ILogger<SegmentStore> logger)
    {
        _dbPath = Path.Combine(appPaths.DataPath, "skipme-segments.json");
        _logger = logger;
        Load();
    }

    /// <summary>
    /// Returns the stored segments for a Jellyfin item, or <c>null</c> if none are stored.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>A read-only list of segments, or <c>null</c>.</returns>
    public IReadOnlyList<StoredSegment>? GetSegments(Guid itemId)
    {
        lock (_syncRoot)
        {
            return _segments.TryGetValue(itemId, out var segments) ? segments : null;
        }
    }

    /// <summary>
    /// Atomically replaces the entire segment store with a new set of data and persists it to disk.
    /// Called by the sync task after a successful full library scan.
    /// </summary>
    /// <param name="newSegments">The new segment data keyed by Jellyfin item ID.</param>
    /// <returns>A task that completes when the data has been persisted.</returns>
    public async Task ReplaceAllAsync(Dictionary<Guid, List<StoredSegment>> newSegments)
    {
        lock (_syncRoot)
        {
            _segments = newSegments;
        }

        await SaveAsync().ConfigureAwait(false);
    }

    private async Task SaveAsync()
    {
        Dictionary<Guid, List<StoredSegment>> snapshot;
        lock (_syncRoot)
        {
            snapshot = _segments;
        }

        var tempPath = _dbPath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
            File.Move(tempPath, _dbPath, overwrite: true);
            _logger.LogInformation(
                "Saved {Count} item(s) to segment store at {Path}",
                snapshot.Count,
                _dbPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to save segment store to {Path}", _dbPath);
        }
    }

    private void Load()
    {
        if (!File.Exists(_dbPath))
        {
            _logger.LogDebug("Segment store file not found at {Path}, starting empty", _dbPath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_dbPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<Guid, List<StoredSegment>>>(json);
            if (loaded is not null)
            {
                _segments = loaded;
                _logger.LogInformation(
                    "Loaded {Count} item(s) from segment store at {Path}",
                    _segments.Count,
                    _dbPath);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to load segment store from {Path}, starting empty", _dbPath);
        }
    }
}
