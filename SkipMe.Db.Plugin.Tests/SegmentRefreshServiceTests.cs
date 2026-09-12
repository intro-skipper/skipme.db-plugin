// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkipMe.Db.Plugin.Services;
using Xunit;

namespace SkipMe.Db.Plugin.Tests;

public sealed class SegmentRefreshServiceTests
{
    [Theory]
    [InlineData(true, "IntroSkipperDetectSegmentsTask")]
    [InlineData(false, "TaskExtractMediaSegments")]
    public void RefreshQueuesOnlyTheActiveOwner(bool integrated, string expectedKey)
    {
        var taskManager = CreateTaskManager("IntroSkipperDetectSegmentsTask", "TaskExtractMediaSegments");
        var service = new SegmentRefreshService(taskManager.Object, NullLogger<SegmentRefreshService>.Instance, integrated);

        service.QueueRefresh();

        taskManager.Verify(manager => manager.QueueScheduledTask(It.Is<IScheduledTask>(task => task.Key == expectedKey), It.IsAny<TaskOptions>()), Times.Once);
        taskManager.Verify(manager => manager.QueueScheduledTask(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Once);
    }

    [Fact]
    public void MissingHostTaskDoesNotFallBackToJellyfinScan()
    {
        var taskManager = CreateTaskManager("TaskExtractMediaSegments");
        var service = new SegmentRefreshService(taskManager.Object, NullLogger<SegmentRefreshService>.Instance, true);

        service.QueueRefresh();

        taskManager.Verify(manager => manager.QueueScheduledTask(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    internal static Mock<ITaskManager> CreateTaskManager(params string[] keys)
    {
        var workers = keys.Select(key =>
        {
            var task = new Mock<IScheduledTask>();
            task.SetupGet(value => value.Key).Returns(key);
            var worker = new Mock<IScheduledTaskWorker>();
            worker.SetupGet(value => value.ScheduledTask).Returns(task.Object);
            return worker.Object;
        }).ToArray();
        var manager = new Mock<ITaskManager>();
        manager.SetupGet(value => value.ScheduledTasks).Returns(workers);
        return manager;
    }
}
