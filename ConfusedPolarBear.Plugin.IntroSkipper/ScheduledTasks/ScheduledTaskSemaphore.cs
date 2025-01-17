using System;
using System.Threading;

namespace ConfusedPolarBear.Plugin.IntroSkipper.ScheduledTasks;

internal sealed class ScheduledTaskSemaphore : IDisposable
{
    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    private static bool _isHeld;

    private ScheduledTaskSemaphore()
    {
    }

    public static int CurrentCount => _semaphore.CurrentCount;

    public static IDisposable Acquire(int timeout, CancellationToken cancellationToken)
    {
        _isHeld = _semaphore.Wait(timeout, cancellationToken);
        return new ScheduledTaskSemaphore();
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    public void Dispose()
    {
        if (_isHeld) // Release only if acquired
        {
            _semaphore.Release();
            _isHeld = false;
        }
    }
}
