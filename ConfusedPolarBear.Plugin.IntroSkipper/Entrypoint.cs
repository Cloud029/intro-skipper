using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
using ConfusedPolarBear.Plugin.IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Server entrypoint.
/// </summary>
public class Entrypoint : IHostedService, IDisposable
{
    private readonly ITaskManager _taskManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Entrypoint> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HashSet<Guid> _seasonsToAnalyze = [];
    private readonly Timer _queueTimer;
    private static readonly ManualResetEventSlim _autoTaskCompletEvent = new(false);
    private bool _disposed;
    private bool _analyzeAgain;
    private static CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="Entrypoint"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public Entrypoint(
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILogger<Entrypoint> logger,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;
        _loggerFactory = loggerFactory;

        _queueTimer = new Timer(
                OnTimerCallback,
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Gets State of the automatic task.
    /// </summary>
    public static TaskState AutomaticTaskState
    {
        get
        {
            if (_cancellationTokenSource is not null)
            {
                return _cancellationTokenSource.IsCancellationRequested
                        ? TaskState.Cancelling
                        : TaskState.Running;
            }

            return TaskState.Idle;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemModified;
        _taskManager.TaskCompleted += OnLibraryRefresh;

        FFmpegWrapper.Logger = _logger;

        try
        {
            // Enqueue all episodes at startup to ensure any FFmpeg errors appear as early as possible
            _logger.LogInformation("Running startup enqueue");
            var queueManager = new QueueManager(_loggerFactory.CreateLogger<QueueManager>(), _libraryManager);
            queueManager?.GetMediaItems();
        }
        catch (Exception ex)
        {
            _logger.LogError("Unable to run startup enqueue: {Exception}", ex);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemModified;
        _taskManager.TaskCompleted -= OnLibraryRefresh;

        // Stop the timer
        _queueTimer.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    // Disclose source for inspiration
    // Implementation based on the principles of jellyfin-plugin-media-analyzer:
    // https://github.com/endrl/jellyfin-plugin-media-analyzer

    /// <summary>
    /// Library item was added.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private void OnItemAdded(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        // Don't do anything if auto detection is disabled
        if (!Plugin.Instance!.Configuration.AutoDetectIntros && !Plugin.Instance.Configuration.AutoDetectCredits)
        {
            return;
        }

        // Don't do anything if it's not a supported media type
        if (itemChangeEventArgs.Item is not Episode episode)
        {
            return;
        }

        if (itemChangeEventArgs.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        _seasonsToAnalyze.Add(episode.SeasonId);

        StartTimer();
    }

    /// <summary>
    /// Library item was modified.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private void OnItemModified(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        // Don't do anything if auto detection is disabled
        if (!Plugin.Instance!.Configuration.AutoDetectIntros && !Plugin.Instance.Configuration.AutoDetectCredits)
        {
            return;
        }

        // Don't do anything if it's not a supported media type
        if (itemChangeEventArgs.Item is not Episode episode)
        {
            return;
        }

        if (itemChangeEventArgs.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        _seasonsToAnalyze.Add(episode.SeasonId);

        StartTimer();
    }

    /// <summary>
    /// TaskManager task ended.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="eventArgs">The <see cref="TaskCompletionEventArgs"/>.</param>
    private void OnLibraryRefresh(object? sender, TaskCompletionEventArgs eventArgs)
    {
        // Don't do anything if auto detection is disabled
        if (!Plugin.Instance!.Configuration.AutoDetectIntros && !Plugin.Instance.Configuration.AutoDetectCredits)
        {
            return;
        }

        var result = eventArgs.Result;

        if (result.Key != "RefreshLibrary")
        {
            return;
        }

        if (result.Status != TaskCompletionStatus.Completed)
        {
            return;
        }

        // Unless user initiated, this is likely an overlap
        if (AutomaticTaskState == TaskState.Running)
        {
            return;
        }

        StartTimer();
    }

    /// <summary>
    /// Start timer to debounce analyzing.
    /// </summary>
    private void StartTimer()
    {
        if (AutomaticTaskState == TaskState.Running)
        {
            _analyzeAgain = true;
        }
        else if (AutomaticTaskState == TaskState.Idle)
        {
            _logger.LogDebug("Media Library changed, analyzis will start soon!");
            _queueTimer.Change(TimeSpan.FromMilliseconds(20000), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Wait for timer callback to be completed.
    /// </summary>
    private void OnTimerCallback(object? state)
    {
        try
        {
            PerformAnalysis();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in PerformAnalysis");
        }

        // Clean up
        _cancellationTokenSource = null;
        _autoTaskCompletEvent.Set();
    }

    /// <summary>
    /// Wait for timer to be completed.
    /// </summary>
    private void PerformAnalysis()
    {
        _logger.LogInformation("Initiate automatic analysis task.");
        _autoTaskCompletEvent.Reset();

        using (_cancellationTokenSource = new CancellationTokenSource())
        using (ScheduledTaskSemaphore.Acquire(-1, _cancellationTokenSource.Token))
        {
            var seasonIds = new HashSet<Guid>(_seasonsToAnalyze);
            _seasonsToAnalyze.Clear();

            _analyzeAgain = false;
            var progress = new Progress<double>();
            var modes = new List<AnalysisMode>();
            var tasklogger = _loggerFactory.CreateLogger("DefaultLogger");

            if (Plugin.Instance!.Configuration.AutoDetectIntros && Plugin.Instance.Configuration.AutoDetectCredits)
            {
                modes.Add(AnalysisMode.Introduction);
                modes.Add(AnalysisMode.Credits);
                tasklogger = _loggerFactory.CreateLogger<DetectIntrosCreditsTask>();
            }
            else if (Plugin.Instance.Configuration.AutoDetectIntros)
            {
                modes.Add(AnalysisMode.Introduction);
                tasklogger = _loggerFactory.CreateLogger<DetectIntrosTask>();
            }
            else if (Plugin.Instance.Configuration.AutoDetectCredits)
            {
                modes.Add(AnalysisMode.Credits);
                tasklogger = _loggerFactory.CreateLogger<DetectCreditsTask>();
            }

            var baseCreditAnalyzer = new BaseItemAnalyzerTask(
                    modes.AsReadOnly(),
                    tasklogger,
                    _loggerFactory,
                    _libraryManager);

            baseCreditAnalyzer.AnalyzeItems(progress, _cancellationTokenSource.Token, seasonIds);

            // New item detected, start timer again
            if (_analyzeAgain && !_cancellationTokenSource.IsCancellationRequested)
            {
                _logger.LogInformation("Analyzing ended, but we need to analyze again!");
                StartTimer();
            }
        }
    }

    /// <summary>
    /// Method to cancel the automatic task.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static void CancelAutomaticTask(CancellationToken cancellationToken)
    {
        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                _cancellationTokenSource = null;
            }
        }

        _autoTaskCompletEvent.Wait(TimeSpan.FromSeconds(60), cancellationToken); // Wait for the signal
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Protected dispose.
    /// </summary>
    /// <param name="disposing">Dispose.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _queueTimer.Dispose();
                _cancellationTokenSource?.Dispose();
                _autoTaskCompletEvent.Dispose();
            }

            _disposed = true;
        }
    }
}
