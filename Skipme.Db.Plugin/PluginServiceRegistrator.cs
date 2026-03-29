// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Skipme.Db.Plugin.Providers;
using Skipme.Db.Plugin.Services;

namespace Skipme.Db.Plugin;

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
        serviceCollection.AddSingleton<IMediaSegmentProvider, SegmentProvider>();
    }
}
