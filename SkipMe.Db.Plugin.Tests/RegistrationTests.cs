// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Reflection;
using System.Reflection.Emit;
using System.Xml;
using System.Xml.Serialization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SkipMe.Db.Plugin.Configuration;
using SkipMe.Db.Plugin.Providers;
using SkipMe.Db.Plugin.Services;
using Xunit;

namespace SkipMe.Db.Plugin.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void CompatibleHostDoesNotActivateIntegrationByDefault()
    {
        Assert.False(new PluginConfiguration().EnableIntroSkipperIntegration);
        var services = CreateServices(enableIntegration: false);
        var assembly = BuildHost((_, _) => throw new InvalidOperationException("Opt-in is required"));

        PluginServiceRegistrator.RegisterServices(services, [assembly]);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IMediaSegmentProvider));
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(SegmentHandoverService));
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<SegmentRefreshService>().IsIntegrated);
    }

    [Theory]
    [InlineData("<PluginConfiguration />")]
    [InlineData("<PluginConfiguration><EnableIntroSkipperIntegration>false</EnableIntroSkipperIntegration></PluginConfiguration>")]
    public void ExistingConfigurationWithoutOptInKeepsStandaloneProvider(string xml)
    {
        using var reader = new StringReader(xml);
        var configuration = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;
        var services = CreateServices(enableIntegration: configuration.EnableIntroSkipperIntegration);
        var calls = 0;
        var assembly = BuildHost((_, _) => calls++);

        PluginServiceRegistrator.RegisterServices(services, [assembly]);

        Assert.Equal(0, calls);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IMediaSegmentProvider));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("malformed")]
    [InlineData("deserialization")]
    public void UnreadableStartupConfigurationCannotOptIn(string failure)
    {
        var services = CreateServices();
        Exception exception = failure switch
        {
            "missing" => new FileNotFoundException(),
            "unreadable" => new UnauthorizedAccessException(),
            "malformed" => new XmlException(),
            _ => new InvalidOperationException(),
        };
        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(value => value.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Throws(exception);
        services.AddSingleton(serializer.Object);
        var calls = 0;
        var assembly = BuildHost((_, _) => calls++);

        PluginServiceRegistrator.RegisterServices(services, [assembly]);

        Assert.Equal(0, calls);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IMediaSegmentProvider));
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(SegmentHandoverService));
    }

    [Fact]
    public void StartupUsesRegisteredInstancesWithoutBuildingAServiceProvider()
    {
        var services = CreateServices();
        var factoryCalls = 0;
        services.AddSingleton<IXmlSerializer>(_ =>
        {
            factoryCalls++;
            throw new InvalidOperationException("Do not instantiate services during registration");
        });
        var calls = 0;
        var assembly = BuildHost((_, _) => calls++);

        PluginServiceRegistrator.RegisterServices(services, [assembly]);

        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, calls);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IMediaSegmentProvider));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntegrationPreferenceRoundTripsWithExistingExclusions(bool enabled)
    {
        var configuration = new PluginConfiguration { EnableIntroSkipperIntegration = enabled };
        configuration.DisabledSeriesIds.Add(Guid.NewGuid());
        configuration.DisabledSeasonIds.Add(Guid.NewGuid());
        configuration.DisabledMovieIds.Add(Guid.NewGuid());
        configuration.EnabledSpecialsSeasonIds.Add(Guid.NewGuid());
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, configuration);
        using var reader = new StringReader(writer.ToString());

        var restored = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.Equal(enabled, restored.EnableIntroSkipperIntegration);
        Assert.Equal(configuration.DisabledSeriesIds, restored.DisabledSeriesIds);
        Assert.Equal(configuration.DisabledSeasonIds, restored.DisabledSeasonIds);
        Assert.Equal(configuration.DisabledMovieIds, restored.DisabledMovieIds);
        Assert.Equal(configuration.EnabledSpecialsSeasonIds, restored.EnabledSpecialsSeasonIds);
    }

    [Fact]
    public void AbsentHostPreservesStandaloneProvider()
    {
        var services = CreateServices();
        PluginServiceRegistrator.RegisterServices(services, []);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IMediaSegmentProvider));
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(SegmentHandoverService));
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<SegmentRefreshService>().IsIntegrated);
    }

    [Theory]
    [InlineData("OtherPlugin", "RegisterV1", false)]
    [InlineData("IntroSkipper", "RegisterV2", false)]
    [InlineData("IntroSkipper", "RegisterV1", true)]
    public void IncompatibleHostPreservesStandaloneProvider(string assemblyName, string methodName, bool returnsValue)
    {
        var services = CreateServices();
        var assembly = BuildHost((_, _) => throw new InvalidOperationException("Must not be invoked"), assemblyName, methodName, returnsValue);
        PluginServiceRegistrator.RegisterServices(services, [assembly]);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IMediaSegmentProvider));
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<SegmentRefreshService>().IsIntegrated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompatibleHostRegistersOnlyConcreteProviderRegardlessOfRegistratorOrder(bool hostFirst)
    {
        var services = CreateServices();
        var calls = 0;
        var assembly = BuildHost((collection, factory) =>
        {
            calls++;
            collection.AddSingleton<Func<IServiceProvider, IMediaSegmentProvider>>(factory);
        });
        if (hostFirst)
        {
            services.AddSingleton(new HostService());
        }

        PluginServiceRegistrator.RegisterServices(services, [assembly]);
        PluginServiceRegistrator.RegisterServices(services, [assembly]);
        if (!hostFirst)
        {
            services.AddSingleton(new HostService());
        }

        Assert.Equal(1, calls);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IMediaSegmentProvider));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(SegmentProvider));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(SegmentHandoverService));
        var segmentProvider = new SegmentProvider(null!, null!, null!);
        services.AddSingleton(segmentProvider);
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<SegmentRefreshService>().IsIntegrated);
        Assert.Same(segmentProvider, provider.GetRequiredService<Func<IServiceProvider, IMediaSegmentProvider>>()(provider));
        Assert.NotNull(provider.GetRequiredService<HostService>());
    }

    [Fact]
    public void FailedHostRegistrationDoesNotLeavePartialServices()
    {
        var services = CreateServices();
        var assembly = BuildHost((collection, _) =>
        {
            collection.Clear();
            collection.AddSingleton(new HostService());
            throw new InvalidOperationException("Incompatible host");
        });
        PluginServiceRegistrator.RegisterServices(services, [assembly]);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(HostService));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(SegmentStore));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IMediaSegmentProvider));
    }

    [Fact]
    public void PluginHasNoIntroSkipperAssemblyReference()
    {
        Assert.DoesNotContain(typeof(Plugin).Assembly.GetReferencedAssemblies(), assembly => assembly.Name == "IntroSkipper");
    }

    private static ServiceCollection CreateServices(bool enableIntegration = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<ITaskManager>());
        services.AddSingleton(Mock.Of<IApplicationPaths>(paths => paths.PluginConfigurationsPath == "/config/plugins"));
        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(value => value.DeserializeFromFile(typeof(PluginConfiguration), Path.Combine("/config/plugins", "SkipMe.Db.Plugin.xml")))
            .Returns(new PluginConfiguration { EnableIntroSkipperIntegration = enableIntegration });
        services.AddSingleton(serializer.Object);
        return services;
    }

    private static Assembly BuildHost(
        Action<IServiceCollection, Func<IServiceProvider, IMediaSegmentProvider>> callback,
        string assemblyName = "IntroSkipper",
        string methodName = "RegisterV1",
        bool returnsValue = false)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("Host").DefineType("IntroSkipper.Integrations.SkipMeIntegration", TypeAttributes.Public | TypeAttributes.Sealed);
        var callbackType = callback.GetType();
        var field = type.DefineField("Callback", callbackType, FieldAttributes.Public | FieldAttributes.Static);
        var method = type.DefineMethod(methodName, MethodAttributes.Public | MethodAttributes.Static, returnsValue ? typeof(bool) : typeof(void), [typeof(IServiceCollection), typeof(Func<IServiceProvider, IMediaSegmentProvider>)]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, callbackType.GetMethod("Invoke")!);
        if (returnsValue)
        {
            il.Emit(OpCodes.Ldc_I4_1);
        }

        il.Emit(OpCodes.Ret);
        type.CreateType()!.GetField("Callback")!.SetValue(null, callback);
        return assembly;
    }

    private sealed class HostService;
}
