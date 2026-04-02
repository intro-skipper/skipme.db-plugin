// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SkipMe.Db.Plugin.Models;

namespace SkipMe.Db.Plugin.Services;

/// <summary>
/// SQLite-backed store for crowd-sourced media segments retrieved from the SkipMe.db API.
/// Segment lists are keyed by Jellyfin item ID and persisted to a SQLite database in the
/// data directory.  The schema is indexed on ItemId so lookups remain fast regardless of
/// library size.
/// </summary>
public sealed class SegmentStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SegmentStore> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentStore"/> class.
    /// Opens or creates the SQLite database and ensures the schema is up to date.
    /// </summary>
    /// <param name="appPaths">Application paths used to locate the data directory.</param>
    /// <param name="logger">The logger.</param>
    public SegmentStore(IApplicationPaths appPaths, ILogger<SegmentStore> logger)
    {
        _logger = logger;

        var dbPath = Path.Combine(appPaths.DataPath, "skipme-segments.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        InitializeSchema();
    }

    /// <summary>
    /// Returns the stored segments for a Jellyfin item, or <c>null</c> if none are stored.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>A read-only list of segments, or <c>null</c>.</returns>
    public IReadOnlyList<StoredSegment>? GetSegments(Guid itemId)
    {
        var key = itemId.ToString();
        _semaphore.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Type, StartMs, EndMs FROM Segments WHERE ItemId = @itemId";
            cmd.Parameters.AddWithValue("@itemId", key);

            using var reader = cmd.ExecuteReader();
            List<StoredSegment>? results = null;
            while (reader.Read())
            {
                results ??= [];
                results.Add(new StoredSegment
                {
                    Type = reader.GetString(0),
                    StartMs = reader.GetInt64(1),
                    EndMs = reader.GetInt64(2),
                });
            }

            return results;
        }
        finally
        {
            _semaphore.Release();
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
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var dbTx = await _connection.BeginTransactionAsync().ConfigureAwait(false);
            var sqliteTx = (SqliteTransaction)dbTx;
            await using (dbTx.ConfigureAwait(false))
            {
                try
                {
                    using (var deleteCmd = _connection.CreateCommand())
                    {
                        deleteCmd.Transaction = sqliteTx;
                        deleteCmd.CommandText = "DELETE FROM Segments";
                        await deleteCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    using (var insertCmd = _connection.CreateCommand())
                    {
                        insertCmd.Transaction = sqliteTx;
                        insertCmd.CommandText =
                            "INSERT INTO Segments (ItemId, Type, StartMs, EndMs) VALUES (@itemId, @type, @startMs, @endMs)";
                        var pItemId = insertCmd.Parameters.Add("@itemId", SqliteType.Text);
                        var pType = insertCmd.Parameters.Add("@type", SqliteType.Text);
                        var pStartMs = insertCmd.Parameters.Add("@startMs", SqliteType.Integer);
                        var pEndMs = insertCmd.Parameters.Add("@endMs", SqliteType.Integer);

                        foreach (var (itemId, segments) in newSegments)
                        {
                            pItemId.Value = itemId.ToString();
                            foreach (var seg in segments)
                            {
                                pType.Value = seg.Type;
                                pStartMs.Value = seg.StartMs;
                                pEndMs.Value = seg.EndMs;
                                await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                        }
                    }

                    await dbTx.CommitAsync().ConfigureAwait(false);
                    _logger.LogInformation(
                        "Saved segments for {Count} item(s) to segment database",
                        newSegments.Count);
                }
                catch
                {
                    await dbTx.RollbackAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _semaphore.Dispose();
        _connection.Dispose();
    }

    private void InitializeSchema()
    {
        using var pragmaJournal = _connection.CreateCommand();
        pragmaJournal.CommandText = "PRAGMA journal_mode=WAL";
        pragmaJournal.ExecuteNonQuery();

        using var pragmaSync = _connection.CreateCommand();
        pragmaSync.CommandText = "PRAGMA synchronous=NORMAL";
        pragmaSync.ExecuteNonQuery();

        using var createTable = _connection.CreateCommand();
        createTable.CommandText = """
            CREATE TABLE IF NOT EXISTS Segments (
                ItemId  TEXT    NOT NULL,
                Type    TEXT    NOT NULL,
                StartMs INTEGER NOT NULL,
                EndMs   INTEGER NOT NULL
            )
            """;
        createTable.ExecuteNonQuery();

        using var createIndex = _connection.CreateCommand();
        createIndex.CommandText =
            "CREATE INDEX IF NOT EXISTS IX_Segments_ItemId ON Segments(ItemId)";
        createIndex.ExecuteNonQuery();

        _logger.LogDebug("Segment database schema initialized");
    }
}
