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
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(nameof(SkipMeApiClient));
        serviceCollection.AddSingleton<SkipMeApiClient>();
        serviceCollection.AddSingleton<SegmentStore>();
        serviceCollection.AddSingleton<IMediaSegmentProvider, SegmentProvider>();
        serviceCollection.AddSingleton<IScheduledTask, SyncSegmentsTask>();
    }
}
