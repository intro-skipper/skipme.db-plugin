// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using MediaBrowser.Controller;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkipMe.Db.Plugin.Services;
using Xunit;

namespace SkipMe.Db.Plugin.Tests;

public sealed class SegmentHandoverServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Mock<IDbContextFactory<JellyfinDbContext>> _factory = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly Mock<ITaskManager> _tasks = SegmentRefreshServiceTests.CreateTaskManager("IntroSkipperDetectSegmentsTask", "TaskExtractMediaSegments");
    private readonly DbContextOptions<JellyfinDbContext> _options;
    private readonly NoLockBehavior _locking = new(NullLogger<NoLockBehavior>.Instance);

    public SegmentHandoverServiceTests()
    {
        _connection.Open();
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection);
        _locking.Initialise(builder);
        _options = builder.Options;
        _factory.Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(CreateContext()));
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public void OwnershipIdMatchesJellyfinsProviderNameHash()
    {
        var expected = new Guid(MD5.HashData(Encoding.Unicode.GetBytes("skipme.db"))).ToString("N", CultureInfo.InvariantCulture);
        Assert.Equal(expected, SegmentHandoverService.SkipMeProviderId);
    }

    [Fact]
    public async Task RetirementDeletesOnlySkipMeOwnershipIncludingUnreplacedModes()
    {
        var itemId = Guid.NewGuid();
        var foreign = Row(itemId, "other-provider", MediaSegmentType.Intro);
        var host = Row(itemId, "intro-skipper-provider", MediaSegmentType.Intro);
        var rawName = Row(itemId, "SkipMe.db", MediaSegmentType.Intro);
        await SeedAsync(
            Row(itemId, SegmentHandoverService.SkipMeProviderId, MediaSegmentType.Intro),
            Row(Guid.NewGuid(), SegmentHandoverService.SkipMeProviderId, MediaSegmentType.Outro),
            foreign,
            host,
            rawName);
        using var service = CreateService();

        Assert.Equal(2, await service.RetireLegacySegmentsAsync(CancellationToken.None));
        Assert.Equal(0, await service.RetireLegacySegmentsAsync(CancellationToken.None));

        using var context = CreateContext();
        Assert.Equal(new[] { foreign.Id, host.Id, rawName.Id }.Order(), context.MediaSegments.Select(segment => segment.Id).ToArray().Order());
    }

    [Fact]
    public async Task FailedRetirementCanRetryWithoutDeletingForeignRows()
    {
        var foreign = Row(Guid.NewGuid(), "other-provider", MediaSegmentType.Intro);
        await SeedAsync(Row(Guid.NewGuid(), SegmentHandoverService.SkipMeProviderId, MediaSegmentType.Intro), foreign);
        _factory.SetupSequence(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Transient database failure"))
            .ReturnsAsync(CreateContext());
        using var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetireLegacySegmentsAsync(CancellationToken.None));
        using (var beforeRetry = CreateContext())
        {
            Assert.Equal(2, beforeRetry.MediaSegments.Count());
        }

        Assert.Equal(1, await service.RetireLegacySegmentsAsync(CancellationToken.None));
        using var context = CreateContext();
        Assert.Equal(foreign.Id, Assert.Single(context.MediaSegments).Id);
    }

    [Fact]
    public async Task StartupWaitsForJellyfinTaskInitializationBeforeHandover()
    {
        var observedStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = false;
        _host.SetupGet(host => host.CoreStartupHasCompleted).Returns(() =>
        {
            var result = Volatile.Read(ref ready);
            observedStartup.TrySetResult();
            return result;
        });
        using var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        await observedStartup.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _tasks.Verify(tasks => tasks.QueueScheduledTask(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Never);
        _factory.Verify(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
        Volatile.Write(ref ready, true);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        _tasks.Verify(tasks => tasks.QueueScheduledTask(It.Is<IScheduledTask>(task => task.Key == "IntroSkipperDetectSegmentsTask"), It.IsAny<TaskOptions>()), Times.Once);
        _factory.Verify(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ShutdownBeforeStartupDoesNotQueueOrDelete()
    {
        using var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        _tasks.Verify(tasks => tasks.QueueScheduledTask(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Never);
        _factory.Verify(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose() => _connection.Dispose();

    private JellyfinDbContext CreateContext() => new(_options, NullLogger<JellyfinDbContext>.Instance, Mock.Of<IJellyfinDatabaseProvider>(), _locking);

    private SegmentHandoverService CreateService() => new(
        _host.Object,
        _factory.Object,
        new SegmentRefreshService(_tasks.Object, NullLogger<SegmentRefreshService>.Instance, true),
        NullLogger<SegmentHandoverService>.Instance);

    private async Task SeedAsync(params MediaSegment[] segments)
    {
        using var context = CreateContext();
        context.MediaSegments.AddRange(segments);
        await context.SaveChangesAsync();
    }

    private static MediaSegment Row(Guid itemId, string providerId, MediaSegmentType type) => new()
    {
        Id = Guid.NewGuid(),
        ItemId = itemId,
        Type = type,
        StartTicks = 1000,
        EndTicks = 2000,
        SegmentProviderId = providerId
    };
}
