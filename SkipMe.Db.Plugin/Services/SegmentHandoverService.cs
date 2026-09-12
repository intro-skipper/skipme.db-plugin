// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Jellyfin.Database.Implementations;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SkipMe.Db.Plugin.Services;

internal sealed class SegmentHandoverService(
    IServerApplicationHost applicationHost,
    IDbContextFactory<JellyfinDbContext> contextFactory,
    SegmentRefreshService segmentRefresh,
    ILogger<SegmentHandoverService> logger) : BackgroundService
{
    internal static readonly string SkipMeProviderId = "skipme.db".GetMD5().ToString("N", CultureInfo.InvariantCulture);

    internal async Task<int> RetireLegacySegmentsAsync(CancellationToken cancellationToken)
    {
        var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.MediaSegments
                .Where(segment => segment.SegmentProviderId == SkipMeProviderId)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!applicationHost.CoreStartupHasCompleted)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }

        stoppingToken.ThrowIfCancellationRequested();
        segmentRefresh.QueueRefresh();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await RetireLegacySegmentsAsync(stoppingToken).ConfigureAwait(false);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Retired {SegmentCount} legacy SkipMe.db Jellyfin segments after activating Intro Skipper integration", deleted);
                }

                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                && (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested))
            {
                logger.LogWarning(exception, "Could not retire legacy SkipMe.db Jellyfin segments; retrying in one minute");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
    }
}
