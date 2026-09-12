// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using SkipMe.Db.Plugin.Models;
using SkipMe.Db.Plugin.Services;
using SkipMe.Db.Plugin.Tasks;
using Xunit;

namespace SkipMe.Db.Plugin.Tests;

public sealed class SyncSegmentsTaskTests
{
    [Theory]
    [InlineData(true, false, "IntroSkipperDetectSegmentsTask")]
    [InlineData(true, true, "IntroSkipperDetectSegmentsTask")]
    [InlineData(false, false, "TaskExtractMediaSegments")]
    public async Task SuccessfulSyncQueuesOwnerAfterReplacingData(bool integrated, bool noResults, string expectedTask)
    {
        await RunSyncAsync(integrated, noResults ? "[null]" : "[{\"intro\":[{\"start_ms\":1000,\"end_ms\":2000}]}]", HttpStatusCode.OK, (store, item, manager) =>
        {
            manager.Verify(value => value.QueueScheduledTask(It.Is<IScheduledTask>(task => task.Key == expectedTask), It.IsAny<TaskOptions>()), Times.Once);
            manager.Verify(value => value.QueueScheduledTask(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Once);
            if (noResults)
            {
                Assert.Null(store.GetSegments(item.Id));
            }
            else
            {
                Assert.Equal(1000, Assert.Single(store.GetSegments(item.Id)!).StartMs);
            }
        });
    }

    [Fact]
    public async Task FailedSyncKeepsDataAndDoesNotQueueDetection()
    {
        await RunSyncAsync(true, "{}", HttpStatusCode.ServiceUnavailable, (store, item, manager) =>
        {
            manager.Verify(value => value.QueueScheduledTask(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Never);
            Assert.Equal(500, Assert.Single(store.GetSegments(item.Id)!).StartMs);
        });
    }

    private static async Task RunSyncAsync(bool integrated, string json, HttpStatusCode status, Action<SegmentStore, Movie, Mock<ITaskManager>> verify)
    {
        var directory = Path.Combine(Path.GetTempPath(), "skipme-sync-tests", Guid.NewGuid().ToString("N"));
        var paths = Mock.Of<IApplicationPaths>(value => value.DataPath == directory);
        try
        {
            using var store = new SegmentStore(paths, NullLogger<SegmentStore>.Instance);
            var item = new Movie { Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromMinutes(90).Ticks, ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" } };
            await store.ReplaceAllAsync(new Dictionary<Guid, List<StoredSegment>>
            {
                [item.Id] = [new() { Type = "intro", StartMs = 500, EndMs = 900 }]
            });
            var library = new Mock<ILibraryManager>();
            library.Setup(value => value.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Movie) ? [item] : []);
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(() => new HttpResponseMessage(status) { Content = new StringContent(json) });
            using var client = new HttpClient(handler.Object);
            var factory = Mock.Of<IHttpClientFactory>(value => value.CreateClient(nameof(SkipMeApiClient)) == client);
            var manager = SegmentRefreshServiceTests.CreateTaskManager("IntroSkipperDetectSegmentsTask", "TaskExtractMediaSegments");
            var task = new SyncSegmentsTask(
                library.Object,
                new SkipMeApiClient(factory, NullLogger<SkipMeApiClient>.Instance),
                store,
                new SegmentRefreshService(manager.Object, NullLogger<SegmentRefreshService>.Instance, integrated),
                NullLogger<SyncSegmentsTask>.Instance);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            verify(store, item, manager);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
