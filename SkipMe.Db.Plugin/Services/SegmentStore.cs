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
    private const string LastSuccessfulSyncUtcKey = "LastSuccessfulSyncUtc";
    private const long ShareDedupToleranceMs = 1000;

    private readonly SqliteConnection _connection;
    private readonly ILogger<SegmentStore> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentStore"/> class.
    /// Opens or creates the SQLite database and ensures the schema is up to date.
    /// Also performs one-time migration steps: moves the database from the root
    /// data directory into the <c>introskipper</c> subdirectory if needed, and
    /// removes the obsolete JSON store that predated the SQLite implementation.
    /// </summary>
    /// <param name="appPaths">Application paths used to locate the data directory.</param>
    /// <param name="logger">The logger.</param>
    public SegmentStore(IApplicationPaths appPaths, ILogger<SegmentStore> logger)
    {
        _logger = logger;

        var subDir = Path.Combine(appPaths.DataPath, "introskipper");
        Directory.CreateDirectory(subDir);

        var newDbPath = Path.Combine(subDir, "skipme-segments.db");
        var oldDbPath = Path.Combine(appPaths.DataPath, "skipme-segments.db");
        var oldJsonPath = Path.Combine(appPaths.DataPath, "skipme-segments.json");

        // Move the database from the root data directory into the introskipper
        // subdirectory.  Only migrate when the old file exists and the new one
        // does not, to avoid clobbering a database that was already migrated.
        if (File.Exists(oldDbPath) && !File.Exists(newDbPath))
        {
            try
            {
                File.Move(oldDbPath, newDbPath);
                _logger.LogInformation("Migrated segment database to {Path}", newDbPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to migrate segment database from {OldPath} to {NewPath}", oldDbPath, newDbPath);
            }
        }

        // Remove the obsolete JSON store that was replaced by the SQLite database.
        if (File.Exists(oldJsonPath))
        {
            try
            {
                File.Delete(oldJsonPath);
                _logger.LogInformation("Removed obsolete JSON segment store at {Path}", oldJsonPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to remove obsolete JSON segment store at {Path}", oldJsonPath);
            }
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = newDbPath,
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

    /// <summary>
    /// Returns the timestamp of the last successful full sync, or <c>null</c> if unavailable.
    /// </summary>
    /// <returns>The UTC timestamp of the last successful sync, or <c>null</c>.</returns>
    public DateTimeOffset? GetLastSuccessfulSyncUtc()
    {
        _semaphore.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Metadata WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", LastSuccessfulSyncUtcKey);
            var raw = cmd.ExecuteScalar() as string;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return DateTimeOffset.TryParseExact(raw, "O", null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Stores the timestamp of the last successful full sync in UTC.
    /// </summary>
    /// <param name="timestampUtc">The UTC timestamp to store.</param>
    /// <returns>A task that completes when the value is persisted.</returns>
    public async Task SetLastSuccessfulSyncUtcAsync(DateTimeOffset timestampUtc)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Metadata (Key, Value)
                VALUES (@key, @value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
                """;
            cmd.Parameters.AddWithValue("@key", LastSuccessfulSyncUtcKey);
            cmd.Parameters.AddWithValue("@value", timestampUtc.ToUniversalTime().ToString("O"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Filters out fingerprints that are already present in the local share history
    /// within ±1 second for start, end, and duration.
    /// </summary>
    /// <param name="candidates">Candidate fingerprints.</param>
    /// <returns>Only fingerprints that have not already been shared.</returns>
    public IReadOnlyList<SharedUploadFingerprint> GetUnsharedFingerprints(IReadOnlyList<SharedUploadFingerprint> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var result = new List<SharedUploadFingerprint>(candidates.Count);

        _semaphore.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT 1
                FROM SharedUploads
                WHERE ItemId = @itemId
                  AND Segment = @segment
                  AND ABS(StartMs - @startMs) <= @tol
                  AND ABS(EndMs - @endMs) <= @tol
                  AND ABS(DurationMs - @durationMs) <= @tol
                LIMIT 1
                """;

            var pItemId = cmd.Parameters.Add("@itemId", SqliteType.Text);
            var pSegment = cmd.Parameters.Add("@segment", SqliteType.Text);
            var pStartMs = cmd.Parameters.Add("@startMs", SqliteType.Integer);
            var pEndMs = cmd.Parameters.Add("@endMs", SqliteType.Integer);
            var pDurationMs = cmd.Parameters.Add("@durationMs", SqliteType.Integer);
            var pTolerance = cmd.Parameters.Add("@tol", SqliteType.Integer);
            pTolerance.Value = ShareDedupToleranceMs;

            foreach (var candidate in candidates)
            {
                pItemId.Value = candidate.ItemId.ToString();
                pSegment.Value = candidate.Segment;
                pStartMs.Value = candidate.StartMs;
                pEndMs.Value = candidate.EndMs;
                pDurationMs.Value = candidate.DurationMs;

                var exists = cmd.ExecuteScalar() is not null;
                if (!exists)
                {
                    result.Add(candidate);
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }

        return result;
    }

    /// <summary>
    /// Records shared fingerprints in the local share history table.
    /// </summary>
    /// <param name="fingerprints">Fingerprints to record.</param>
    /// <returns>A task that completes when the rows are persisted.</returns>
    public async Task RecordSharedFingerprintsAsync(IReadOnlyList<SharedUploadFingerprint> fingerprints)
    {
        if (fingerprints.Count == 0)
        {
            return;
        }

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var dbTx = await _connection.BeginTransactionAsync().ConfigureAwait(false);
            var sqliteTx = (SqliteTransaction)dbTx;
            await using (dbTx.ConfigureAwait(false))
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = sqliteTx;
                cmd.CommandText = """
                    INSERT INTO SharedUploads (ItemId, Segment, StartMs, EndMs, DurationMs, SharedAtUtc)
                    VALUES (@itemId, @segment, @startMs, @endMs, @durationMs, @sharedAtUtc)
                    """;

                var pItemId = cmd.Parameters.Add("@itemId", SqliteType.Text);
                var pSegment = cmd.Parameters.Add("@segment", SqliteType.Text);
                var pStartMs = cmd.Parameters.Add("@startMs", SqliteType.Integer);
                var pEndMs = cmd.Parameters.Add("@endMs", SqliteType.Integer);
                var pDurationMs = cmd.Parameters.Add("@durationMs", SqliteType.Integer);
                var pSharedAtUtc = cmd.Parameters.Add("@sharedAtUtc", SqliteType.Text);
                // Keep one consistent timestamp for the full batch insertion transaction.
                var sharedAtUtc = DateTimeOffset.UtcNow.ToString("O");

                foreach (var fingerprint in fingerprints)
                {
                    pItemId.Value = fingerprint.ItemId.ToString();
                    pSegment.Value = fingerprint.Segment;
                    pStartMs.Value = fingerprint.StartMs;
                    pEndMs.Value = fingerprint.EndMs;
                    pDurationMs.Value = fingerprint.DurationMs;
                    pSharedAtUtc.Value = sharedAtUtc;
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await dbTx.CommitAsync().ConfigureAwait(false);
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

        using var createMetadataTable = _connection.CreateCommand();
        createMetadataTable.CommandText = """
            CREATE TABLE IF NOT EXISTS Metadata (
                Key   TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL
            )
            """;
        createMetadataTable.ExecuteNonQuery();

        using var createSharedUploads = _connection.CreateCommand();
        createSharedUploads.CommandText = """
            CREATE TABLE IF NOT EXISTS SharedUploads (
                ItemId      TEXT    NOT NULL,
                Segment     TEXT    NOT NULL,
                StartMs     INTEGER NOT NULL,
                EndMs       INTEGER NOT NULL,
                DurationMs  INTEGER NOT NULL,
                SharedAtUtc TEXT    NOT NULL
            )
            """;
        createSharedUploads.ExecuteNonQuery();

        using var createSharedUploadsIndex = _connection.CreateCommand();
        createSharedUploadsIndex.CommandText =
            "CREATE INDEX IF NOT EXISTS IX_SharedUploads_ItemSegment ON SharedUploads(ItemId, Segment)";
        createSharedUploadsIndex.ExecuteNonQuery();

        _logger.LogDebug("Segment database schema initialized");
    }
}
