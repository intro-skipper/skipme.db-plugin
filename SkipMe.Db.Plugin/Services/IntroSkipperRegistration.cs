// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SkipMe.Db.Plugin.Configuration;
using SkipMe.Db.Plugin.Providers;

namespace SkipMe.Db.Plugin.Services;

internal static class IntroSkipperRegistration
{
    internal static bool TryRegister(IServiceCollection services, IEnumerable<Assembly> assemblies)
    {
        if (!IsEnabled(services))
        {
            return false;
        }

        foreach (var assembly in assemblies)
        {
            if (!string.Equals(assembly.GetName().Name, "IntroSkipper", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var method = assembly.GetType("IntroSkipper.Integrations.SkipMeIntegration")?.GetMethod(
                    "RegisterV1",
                    BindingFlags.Public | BindingFlags.Static,
                    [typeof(IServiceCollection), typeof(Func<IServiceProvider, IMediaSegmentProvider>)]);
                if (method is null || method.ReturnType != typeof(void) || method.ContainsGenericParameters)
                {
                    continue;
                }

                var register = method.CreateDelegate<Action<IServiceCollection, Func<IServiceProvider, IMediaSegmentProvider>>>();
                IServiceCollection candidate = new ServiceCollection();
                foreach (var descriptor in services)
                {
                    candidate.Add(descriptor);
                }

                register(candidate, static provider => provider.GetRequiredService<SegmentProvider>());
                services.Clear();
                foreach (var descriptor in candidate)
                {
                    services.Add(descriptor);
                }

                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsEnabled(IServiceCollection services)
    {
        var paths = services.LastOrDefault(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IApplicationPaths))?.ImplementationInstance as IApplicationPaths;
        var serializer = services.LastOrDefault(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IXmlSerializer))?.ImplementationInstance as IXmlSerializer;
        if (paths is null || serializer is null)
        {
            return false;
        }

        try
        {
            var fileName = Path.ChangeExtension(Path.GetFileName(typeof(Plugin).Assembly.Location), ".xml");
            var path = Path.Combine(paths.PluginConfigurationsPath, fileName);
            return serializer.DeserializeFromFile(typeof(PluginConfiguration), path) is PluginConfiguration { EnableIntroSkipperIntegration: true };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}
