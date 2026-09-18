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
public sealed class SyncSegmentsTaskScheduleService : BackgroundService
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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var warned = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var worker = _taskManager.ScheduledTasks
                .FirstOrDefault(task => task.ScheduledTask is SyncSegmentsTask);

            if (worker is not null)
            {
                worker.Triggers = worker.ScheduledTask.GetDefaultTriggers().ToArray();

                if (worker.LastExecutionResult is null && worker.State == TaskState.Idle)
                {
                    _logger.LogInformation("Running the initial SkipMe.db sync.");
                    _taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
                }

                return;
            }

            if (!warned)
            {
                _logger.LogDebug("Waiting for Jellyfin to register the SkipMe.db sync scheduled task.");
                warned = true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
