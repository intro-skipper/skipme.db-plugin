// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace SkipMe.Db.Plugin.Services;

/// <summary>
/// Routes segment refreshes to the active owner of SkipMe.db analysis.
/// </summary>
public sealed class SegmentRefreshService
{
    private const string MediaSegmentScanTaskKey = "TaskExtractMediaSegments";
    private const string IntroSkipperTaskKey = "IntroSkipperDetectSegmentsTask";

    private readonly ITaskManager _taskManager;
    private readonly ILogger<SegmentRefreshService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentRefreshService"/> class.
    /// </summary>
    /// <param name="taskManager">The Jellyfin task manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="isIntegrated">Whether Intro Skipper accepted the provider registration.</param>
    public SegmentRefreshService(ITaskManager taskManager, ILogger<SegmentRefreshService> logger, bool isIntegrated)
    {
        _taskManager = taskManager;
        _logger = logger;
        IsIntegrated = isIntegrated;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("SkipMe.db segment analysis is running in {Mode} mode", isIntegrated ? "Intro Skipper" : "standalone");
        }
    }

    /// <summary>
    /// Gets a value indicating whether Intro Skipper owns SkipMe.db analysis.
    /// </summary>
    public bool IsIntegrated { get; }

    /// <summary>
    /// Queues the active segment analysis task after a data or configuration change.
    /// </summary>
    public void QueueRefresh()
    {
        var taskKey = IsIntegrated ? IntroSkipperTaskKey : MediaSegmentScanTaskKey;
        var worker = _taskManager.ScheduledTasks
            .FirstOrDefault(task => string.Equals(task.ScheduledTask.Key, taskKey, StringComparison.Ordinal));
        if (worker is null)
        {
            _logger.LogWarning("Could not find scheduled task with key '{TaskKey}' — segment analysis will not be triggered", taskKey);
            return;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Queuing segment analysis ('{TaskKey}')", taskKey);
        }

        _taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
    }
}
