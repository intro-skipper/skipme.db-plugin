// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkipMe.Db.Plugin.Providers;
using SkipMe.Db.Plugin.Services;
using SkipMe.Db.Plugin.Tasks;

namespace SkipMe.Db.Plugin;

/// <summary>
/// Registers SkipMe.db plugin services with the Jellyfin dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    private static readonly TimeSpan SkipMeApiTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TvMazeTimeout = TimeSpan.FromSeconds(15);

    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(nameof(SkipMeApiClient))
            .ConfigureHttpClient(c =>
            {
                c.Timeout = SkipMeApiTimeout;
                c.DefaultRequestHeaders.UserAgent.ParseAdd("SkipMe.db/0.0");
            });
        serviceCollection.AddSingleton<SkipMeApiClient>();
        serviceCollection.AddHttpClient(nameof(TvMazeClient))
            .ConfigureHttpClient(c =>
            {
                c.Timeout = TvMazeTimeout;
                c.DefaultRequestHeaders.UserAgent.ParseAdd("SkipMe.db/0.0");
            });
        serviceCollection.AddSingleton<TvMazeClient>();
        serviceCollection.AddSingleton<SegmentStore>();
        serviceCollection.AddSingleton<ShareSubmissionService>();
        serviceCollection.AddSingleton<IMediaSegmentProvider, SegmentProvider>();
        serviceCollection.AddSingleton<IScheduledTask, SyncSegmentsTask>();
    }
}
