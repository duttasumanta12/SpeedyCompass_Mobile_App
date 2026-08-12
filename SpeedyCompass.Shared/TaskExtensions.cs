using System;
using System.Threading.Tasks;

namespace SpeedyCompass.Shared;

public static class TaskExtensions
{
    /// <summary>
    /// Safely executes a Task in the background. Catches and logs exceptions, 
    /// but gracefully ignores standard Task Cancellations.
    /// </summary>
    public static void SafeFireAndForget(this Task task, Action<Exception> onException = null)
    {
        task.ContinueWith(t =>
        {
            var ex = t.Exception?.Flatten().InnerException ?? t.Exception;

            if (ex != null)
            {
                // We expect cancellations when the UI tears down. Ignore them!
                if (ex is TaskCanceledException || ex is OperationCanceledException) return;

                if (onException != null)
                {
                    onException(ex);
                }
                else
                {
                    AppLogger.Error("SafeFireAndForget", ex, "Unhandled exception in background task.");
                }
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
}