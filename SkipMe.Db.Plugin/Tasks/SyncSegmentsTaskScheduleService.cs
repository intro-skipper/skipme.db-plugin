// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SkipMe.Db.Plugin.Tasks;

/// <summary>
/// Applies the current SkipMe.db task schedule to existing Jellyfin installations.
/// </summary>
public sealed class SyncSegmentsTaskScheduleService : IHostedService
{
    private readonly ITaskManager _taskManager;
    private readonly ILogger<SyncSegmentsTaskScheduleService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncSegmentsTaskScheduleService"/> class.
    /// </summary>
    /// <param name="taskManager">The Jellyfin task manager.</param>
    /// <param name="logger">The logger.</param>
    public SyncSegmentsTaskScheduleService(
        ITaskManager taskManager,
        ILogger<SyncSegmentsTaskScheduleService> logger)
    {
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var worker = _taskManager.ScheduledTasks
            .FirstOrDefault(task => string.Equals(task.ScheduledTask.Key, SyncSegmentsTask.TaskKey, StringComparison.Ordinal));

        if (worker is null)
        {
            _logger.LogWarning("Could not find the SkipMe.db sync scheduled task while applying its schedule.");
            return Task.CompletedTask;
        }

        worker.Triggers = worker.ScheduledTask.GetDefaultTriggers().ToArray();

        if (worker.LastExecutionResult is null && worker.State == TaskState.Idle)
        {
            _taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
