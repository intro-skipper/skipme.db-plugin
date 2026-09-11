// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkipMe.Db.Plugin.Configuration;
using SkipMe.Db.Plugin.Models;
using SkipMe.Db.Plugin.Providers;
using SkipMe.Db.Plugin.Services;
using Xunit;

namespace SkipMe.Db.Plugin.Tests;

public sealed class SegmentProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "skipme-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<ITaskManager> _tasks = SegmentRefreshServiceTests.CreateTaskManager("IntroSkipperDetectSegmentsTask", "TaskExtractMediaSegments");
    private readonly SegmentStore _store;
    private readonly SegmentProvider _provider;
    private readonly Mock<IApplicationPaths> _paths = new();
    private readonly Mock<IXmlSerializer> _serializer = new();
    private readonly Plugin _plugin;

    public SegmentProviderTests()
    {
        _paths.SetupGet(paths => paths.DataPath).Returns(_directory);
        _paths.SetupGet(paths => paths.PluginsPath).Returns(_directory);
        _paths.SetupGet(paths => paths.PluginConfigurationsPath).Returns(_directory);
        _serializer.Setup(serializer => serializer.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration());
        _library.Setup(library => library.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
        BaseItem.LibraryManager = _library.Object;
        _store = new SegmentStore(_paths.Object, NullLogger<SegmentStore>.Instance);
        _provider = new SegmentProvider(_store, _library.Object, NullLogger<SegmentProvider>.Instance);
        _plugin = CreatePlugin(true);
    }

    [Theory]
    [InlineData("SkipMe.db")]
    [InlineData("SKIPME.DB")]
    public async Task LibraryDisabledProviderSuppressesStoredSegments(string disabledName)
    {
        var item = new Movie { Id = Guid.NewGuid() };
        await StoreAsync(item);
        _library.Setup(library => library.GetLibraryOptions(item)).Returns(new LibraryOptions { DisabledMediaSegmentProviders = [disabledName] });

        Assert.Empty(await ReadAsync(item.Id));
        Assert.NotEmpty(_store.GetSegments(item.Id)!);
    }

    [Fact]
    public async Task DisabledMovieSuppressesStoredSegments()
    {
        var item = new Movie { Id = Guid.NewGuid() };
        await StoreAsync(item);
        _plugin.Configuration.DisabledMovieIds.Add(item.Id);

        Assert.Empty(await ReadAsync(item.Id));
    }

    [Fact]
    public async Task DisabledSeriesSuppressesStoredSegments()
    {
        var series = new Series { Id = Guid.NewGuid() };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 1, ParentId = Guid.NewGuid() };
        _library.Setup(library => library.GetItemById(series.Id)).Returns(series);
        await StoreAsync(episode);
        _plugin.Configuration.DisabledSeriesIds.Add(series.Id);

        Assert.Empty(await ReadAsync(episode.Id));
    }

    [Fact]
    public async Task DisabledSeasonSuppressesStoredSegments()
    {
        var episode = CreateEpisode(1);
        await StoreAsync(episode);
        _plugin.Configuration.DisabledSeasonIds.Add(episode.ParentId);

        Assert.Empty(await ReadAsync(episode.Id));
    }

    [Fact]
    public async Task SpecialsStayDisabledUntilExplicitlyEnabled()
    {
        var episode = CreateEpisode(0);
        await StoreAsync(episode);

        Assert.Empty(await ReadAsync(episode.Id));
        _plugin.Configuration.EnabledSpecialsSeasonIds.Add(episode.ParentId);
        Assert.Single(await ReadAsync(episode.Id));
        _plugin.Configuration.DisabledSeasonIds.Add(episode.ParentId);
        Assert.Empty(await ReadAsync(episode.Id));
    }

    [Fact]
    public async Task AllowedMoviePreservesRangesAndTypeMapping()
    {
        var item = new Movie { Id = Guid.NewGuid() };
        _library.Setup(library => library.GetItemById(item.Id)).Returns(item);
        await _store.ReplaceAllAsync(new Dictionary<Guid, List<StoredSegment>>
        {
            [item.Id] = [
                new() { Type = "intro", StartMs = 1001, EndMs = 9009 },
                new() { Type = "credits", StartMs = 10000, EndMs = 20000 },
                new() { Type = "recap", StartMs = 10, EndMs = 100 },
                new() { Type = "preview", StartMs = 20001, EndMs = 30000 },
                new() { Type = "commercial", StartMs = 40000, EndMs = 50000 },
                new() { Type = "unknown", StartMs = 0, EndMs = 100 }
            ]
        });

        var segments = await ReadAsync(item.Id);

        Assert.Equal(5, segments.Count);
        var intro = Assert.Single(segments, segment => segment.Type == MediaSegmentType.Intro);
        Assert.Equal(1001 * TimeSpan.TicksPerMillisecond, intro.StartTicks);
        Assert.Equal(9009 * TimeSpan.TicksPerMillisecond, intro.EndTicks);
        Assert.Contains(segments, segment => segment.Type == MediaSegmentType.Outro);
        Assert.Contains(segments, segment => segment.Type == MediaSegmentType.Recap);
        Assert.Contains(segments, segment => segment.Type == MediaSegmentType.Preview);
        Assert.Contains(segments, segment => segment.Type == MediaSegmentType.Commercial);
    }

    [Fact]
    public async Task MissingItemAndMissingDataReturnNoSegments()
    {
        Assert.Empty(await ReadAsync(Guid.NewGuid()));
        var item = new Movie { Id = Guid.NewGuid() };
        _library.Setup(library => library.GetItemById(item.Id)).Returns(item);
        Assert.Empty(await ReadAsync(item.Id));
    }

    [Fact]
    public async Task SupportsOnlyMoviesAndEpisodes()
    {
        Assert.True(await _provider.Supports(new Movie()));
        Assert.True(await _provider.Supports(new Episode()));
        Assert.False(await _provider.Supports(new Series()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfigurationChangesQueueDetectionOnlyWhenIntegrated(bool integrated)
    {
        var plugin = CreatePlugin(integrated);

        plugin.UpdateConfiguration(new PluginConfiguration());

        _serializer.Verify(serializer => serializer.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()), Times.Once);
        _tasks.Verify(tasks => tasks.QueueScheduledTask(It.Is<IScheduledTask>(task => task.Key == "IntroSkipperDetectSegmentsTask"), It.IsAny<TaskOptions>()), integrated ? Times.Once() : Times.Never());
        _tasks.Verify(tasks => tasks.QueueScheduledTask(It.Is<IScheduledTask>(task => task.Key == "TaskExtractMediaSegments"), It.IsAny<TaskOptions>()), Times.Never);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_directory, true);
    }

    private Plugin CreatePlugin(bool integrated) => new(
        _paths.Object,
        _serializer.Object,
        _library.Object,
        new SegmentRefreshService(_tasks.Object, NullLogger<SegmentRefreshService>.Instance, integrated));

    private Episode CreateEpisode(int seasonNumber)
    {
        var series = new Series { Id = Guid.NewGuid() };
        _library.Setup(library => library.GetItemById(series.Id)).Returns(series);
        return new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = seasonNumber, ParentId = Guid.NewGuid() };
    }

    private Task StoreAsync(BaseItem item)
    {
        _library.Setup(library => library.GetItemById(item.Id)).Returns(item);
        return _store.ReplaceAllAsync(new Dictionary<Guid, List<StoredSegment>>
        {
            [item.Id] = [new() { Type = "intro", StartMs = 1000, EndMs = 2000 }]
        });
    }

    private Task<IReadOnlyList<MediaBrowser.Model.MediaSegments.MediaSegmentDto>> ReadAsync(Guid itemId) =>
        _provider.GetMediaSegments(new MediaSegmentGenerationRequest { ItemId = itemId, ExistingSegments = [] }, CancellationToken.None);
}
