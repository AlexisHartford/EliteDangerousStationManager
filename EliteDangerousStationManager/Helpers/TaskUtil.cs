using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace EliteDangerousStationManager.Helpers
{
    internal static class TaskUtil
    {
        /// <summary>
        /// Fire-and-forget that logs exceptions instead of crashing on UnobservedTaskException.
        /// </summary>
        public static async void SafeForget(
            this Task task,
            string label = "",
            Action<Exception>? logger = null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (logger ?? DefaultLog)(Wrap(label, ex));
            }
        }

        /// <summary>
        /// Same as SafeForget but not an extension (explicit call).
        /// </summary>
        public static void FireAndLog(Task task, string label = "", Action<Exception>? logger = null)
            => SafeForget(task, label, logger);

        /// <summary>
        /// Runs an async action with a timeout. Throws TimeoutException on expiry.
        /// </summary>
        public static async Task WithTimeout(
            Func<CancellationToken, Task> work,
            TimeSpan timeout,
            CancellationToken externalCt = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            var t = work(cts.Token);
            var delay = Task.Delay(timeout, cts.Token);

            var done = await Task.WhenAny(t, delay).ConfigureAwait(false);
            if (done == delay)
            {
                cts.Cancel();
                throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds:N0}s.");
            }
            await t.ConfigureAwait(false);
        }

        /// <summary>
        /// Runs an async func with a timeout and returns its result.
        /// </summary>
        public static async Task<T> WithTimeout<T>(
            Func<CancellationToken, Task<T>> work,
            TimeSpan timeout,
            CancellationToken externalCt = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            var t = work(cts.Token);
            var delay = Task.Delay(timeout, cts.Token);

            var done = await Task.WhenAny(t, delay).ConfigureAwait(false);
            if (done == delay)
            {
                cts.Cancel();
                throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds:N0}s.");
            }
            return await t.ConfigureAwait(false);
        }

        /// <summary>
        /// Retry with exponential backoff. Throws last exception if all retries fail.
        /// </summary>
        public static async Task<T> RunWithRetry<T>(
            Func<CancellationToken, Task<T>> work,
            int maxAttempts = 3,
            TimeSpan? initialDelay = null,
            double backoffFactor = 2.0,
            Func<Exception, bool>? shouldRetryOn = null,
            CancellationToken ct = default,
            Action<string>? logInfo = null,
            Action<Exception>? logWarn = null)
        {
            initialDelay ??= TimeSpan.FromMilliseconds(250);
            shouldRetryOn ??= (_ => true);
            var delay = initialDelay.Value;
            var attempt = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    attempt++;
                    return await work(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < maxAttempts && shouldRetryOn(ex))
                {
                    logWarn?.Invoke(new Exception($"[Retry {attempt}/{maxAttempts}] {ex.Message}", ex));
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * backoffFactor);
                }
            }
        }

        /// <summary>
        /// Debounce: waits for a lull of 'wait' before executing 'action'.
        /// Multiple calls reset the timer.
        /// </summary>
        public static Action DebounceAsync(
            Func<Task> action,
            TimeSpan wait,
            Action<Exception>? logWarn = null)
        {
            CancellationTokenSource? cts = null;
            return () =>
            {
                cts?.Cancel();
                cts = new CancellationTokenSource();
                var token = cts.Token;

                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(wait, token).ConfigureAwait(false);
                        if (!token.IsCancellationRequested)
                            await action().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { /* expected */ }
                    catch (Exception ex) { logWarn?.Invoke(ex); }
                }).SafeForget("DebounceAsync", ex => logWarn?.Invoke(ex));
            };
        }

        /// <summary>
        /// Throttle: ensures 'action' runs at most once per 'interval'; extra calls coalesce to one trailing run.
        /// </summary>
        public static Action ThrottleAsync(
            Func<Task> action,
            TimeSpan interval,
            Action<Exception>? logWarn = null)
        {
            var gate = new SemaphoreSlim(1, 1);
            var lastRun = DateTime.MinValue;
            var pending = false;

            return () =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await gate.WaitAsync().ConfigureAwait(false);
                        var now = DateTime.UtcNow;
                        var elapsed = now - lastRun;

                        if (elapsed >= interval && !pending)
                        {
                            lastRun = now;
                            gate.Release();
                            await action().ConfigureAwait(false);
                        }
                        else
                        {
                            pending = true;
                            gate.Release();

                            var wait = interval - elapsed;
                            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                            await Task.Delay(wait).ConfigureAwait(false);

                            await gate.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                if (pending)
                                {
                                    pending = false;
                                    lastRun = DateTime.UtcNow;
                                    gate.Release();
                                    await action().ConfigureAwait(false);
                                }
                                else
                                {
                                    gate.Release();
                                }
                            }
                            catch
                            {
                                gate.Release();
                                throw;
                            }
                        }
                    }
                    catch (Exception ex) { logWarn?.Invoke(ex); }
                }).SafeForget("ThrottleAsync", ex => logWarn?.Invoke(ex));
            };
        }

        /// <summary>
        /// Await a set of tasks and return exceptions without throwing.
        /// </summary>
        public static async Task<IReadOnlyList<Exception>> WhenAllSafe(IEnumerable<Task> tasks)
        {
            var list = tasks.ToList();
            if (list.Count == 0) return Array.Empty<Exception>();

            try
            {
                await Task.WhenAll(list).ConfigureAwait(false);
                return Array.Empty<Exception>();
            }
            catch
            {
                // Collect all task exceptions
                var errs = new List<Exception>(list.Count);
                foreach (var t in list)
                {
                    if (t.IsFaulted && t.Exception != null)
                        errs.AddRange(t.Exception.InnerExceptions);
                }
                return errs;
            }
        }

        // -----------------------
        // Internals / conveniences
        // -----------------------
        private static Exception Wrap(string label, Exception ex,
            [CallerMemberName] string? caller = null)
        {
            var msg = string.IsNullOrWhiteSpace(label)
                ? $"[TaskUtil:{caller}] {ex.Message}"
                : $"[TaskUtil:{label}] {ex.Message}";
            return new Exception(msg, ex);
        }

        private static void DefaultLog(Exception ex)
        {
            try
            {
                Logger.Log(ex.ToString(), "Warning");
            }
            catch
            {
                Debug.WriteLine(ex.ToString());
            }
        }
    }
}
