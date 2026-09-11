// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Reflection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        RegisterServices(serviceCollection, AppDomain.CurrentDomain.GetAssemblies());
    }

    internal static void RegisterServices(IServiceCollection serviceCollection, IEnumerable<Assembly> assemblies)
    {
        if (serviceCollection.Any(descriptor => descriptor.ServiceType == typeof(SegmentRefreshService)))
        {
            return;
        }

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
        serviceCollection.AddSingleton<SegmentProvider>();
        var isIntegrated = IntroSkipperRegistration.TryRegister(serviceCollection, assemblies);
        if (!isIntegrated)
        {
            serviceCollection.AddSingleton<IMediaSegmentProvider>(static provider => provider.GetRequiredService<SegmentProvider>());
        }
        else
        {
            serviceCollection.AddHostedService<SegmentHandoverService>();
        }

        serviceCollection.AddSingleton(provider => new SegmentRefreshService(
            provider.GetRequiredService<ITaskManager>(),
            provider.GetRequiredService<ILogger<SegmentRefreshService>>(),
            isIntegrated));
        serviceCollection.AddSingleton<IScheduledTask, SyncSegmentsTask>();
    }
}
