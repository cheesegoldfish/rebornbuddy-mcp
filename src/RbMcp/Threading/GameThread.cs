using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Helpers;

namespace RbMcp.Threading;

/// <summary>
/// Serializes every piece of RebornBuddy API access onto a single logical thread.
///
/// Why this exists: HTTP requests arrive on threadpool threads, but RB's caches
/// (ObjectManager, party, directors) are mutated by the botbase pulse thread. Reading
/// them concurrently is a data race, and RB objects are lazy memory-read facades, so a
/// torn read produces garbage rather than an exception.
///
/// Two drain sources, mutually exclusive via <see cref="DrainLock"/>:
///   - Bot running   -> <see cref="DrainFromPulse"/>, called from BotPlugin.OnPulse, i.e.
///                      already on the pulse thread. No pulse of our own; the bot is
///                      keeping caches fresh for us.
///   - Bot stopped   -> the worker thread drains and pulses the caches itself first,
///                      which is what RebornConsole does and is known to work.
///
/// A watchdog covers the nasty middle case: TreeRoot claims to be running but pulses have
/// stalled (bot paused at a breakpoint, stuck behavior, RB minimized-and-throttled). After
/// <see cref="PulseStallMs"/> with no observed pulse we let the worker drain anyway rather
/// than hang every request until timeout.
/// </summary>
internal static class GameThread
{
    private const int PulseStallMs = 2000;
    private const int WorkerIdleMs = 25;

    /// <summary>How often <see cref="PeriodicWork"/> runs, regardless of queued requests.</summary>
    private const int PeriodicIntervalMs = 250;

    private static readonly ConcurrentQueue<WorkItem> Queue = new();
    private static readonly object DrainLock = new();
    private static readonly AutoResetEvent WorkSignal = new(false);
    private static readonly Stopwatch SinceLastPulse = Stopwatch.StartNew();
    private static readonly Stopwatch SincePeriodic = Stopwatch.StartNew();

    /// <summary>
    /// Runs on the game thread every <see cref="PeriodicIntervalMs"/> whether or not
    /// anything was requested. Exists for sampling that has to happen continuously to be
    /// worth anything - aura history cannot be reconstructed after the fact, because by
    /// the time someone asks "what hit me", the aura is gone.
    ///
    /// Keep it cheap. It shares the pulse thread with the botbase.
    /// </summary>
    public static Action PeriodicWork;

    private static Thread _worker;
    private static volatile bool _running;

    /// <summary>Pulse flags used when we drive the pulse ourselves (bot stopped).</summary>
    private const PulseFlags SelfPulse = PulseFlags.ObjectManager
                                         | PulseFlags.GameEvents
                                         | PulseFlags.Windows
                                         | PulseFlags.Party
                                         | PulseFlags.Directors;

    public static void Start()
    {
        if (_running) return;
        _running = true;
        _worker = new Thread(WorkerLoop)
        {
            Name = "RbMcp.GameThread",
            IsBackground = true
        };
        _worker.Start();
    }

    public static void Stop()
    {
        _running = false;
        WorkSignal.Set();

        // Fail any work still queued so in-flight HTTP requests get an answer
        // instead of sitting on their timeout.
        while (Queue.TryDequeue(out var item))
            item.Fail(new InvalidOperationException("RbMcp is shutting down."));
    }

    /// <summary>
    /// Queues <paramref name="fn"/> for execution with RB's caches in a known-good state
    /// and awaits its result. Never call this from inside work already running on the
    /// game thread - it will deadlock against its own queue.
    /// </summary>
    public static Task<T> Invoke<T>(Func<T> fn, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_running)
        {
            tcs.SetException(new InvalidOperationException("RbMcp game thread is not running."));
            return tcs.Task;
        }

        var item = new WorkItem(
            run: () => tcs.TrySetResult(fn()),
            fail: ex => tcs.TrySetException(ex));

        Queue.Enqueue(item);
        WorkSignal.Set();

        return WithTimeout(tcs, timeoutMs);
    }

    private static async Task<T> WithTimeout<T>(TaskCompletionSource<T> tcs, int timeoutMs)
    {
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            throw new TimeoutException(
                $"Timed out after {timeoutMs}ms waiting for the game thread. " +
                (TreeRoot.IsRunning
                    ? "The botbase is running but may be stalled."
                    : "RebornBuddy may be busy or the game may not be attached."));
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Called from BotPlugin.OnPulse - we are on the pulse thread here.</summary>
    public static void DrainFromPulse()
    {
        SinceLastPulse.Restart();

        if (Monitor.TryEnter(DrainLock))
        {
            try
            {
                DrainQueue();
                RunPeriodic();
            }
            finally { Monitor.Exit(DrainLock); }
        }
    }

    private static void RunPeriodic()
    {
        var work = PeriodicWork;
        if (work == null) return;
        if (SincePeriodic.ElapsedMilliseconds < PeriodicIntervalMs) return;

        SincePeriodic.Restart();

        try
        {
            work();
        }
        catch (Exception ex)
        {
            // Never let sampling take down the pulse.
            Logging.Write($"[RbMcp] Periodic work failed: {ex.Message}");
        }
    }

    private static void WorkerLoop()
    {
        while (_running)
        {
            WorkSignal.WaitOne(WorkerIdleMs);

            var hasWork = !Queue.IsEmpty;
            var periodicDue = PeriodicWork != null && SincePeriodic.ElapsedMilliseconds >= PeriodicIntervalMs;
            if (!hasWork && !periodicDue) continue;

            // Hand off to OnPulse while the bot is genuinely pulsing; only step in if it
            // has gone quiet for longer than a pulse should ever take.
            if (TreeRoot.IsRunning && SinceLastPulse.ElapsedMilliseconds < PulseStallMs)
                continue;

            if (!Monitor.TryEnter(DrainLock)) continue;
            try
            {
                // Only worth pulsing when there is a request to serve - sampling reads
                // memory directly and does not need RB's caches refreshed, so pulsing
                // four times a second just to sample would be pure overhead.
                if (hasWork)
                {
                    try
                    {
                        Pulsator.Pulse(SelfPulse);
                    }
                    catch (Exception ex)
                    {
                        Logging.Write($"[RbMcp] Pulse failed: {ex.Message}");
                    }

                    DrainQueue();
                }

                RunPeriodic();
            }
            finally
            {
                Monitor.Exit(DrainLock);
            }
        }
    }

    private static void DrainQueue()
    {
        while (Queue.TryDequeue(out var item))
        {
            try
            {
                item.Run();
            }
            catch (Exception ex)
            {
                item.Fail(ex);
            }
        }
    }

    private sealed class WorkItem
    {
        private readonly Action _run;
        private readonly Action<Exception> _fail;

        public WorkItem(Action run, Action<Exception> fail)
        {
            _run = run;
            _fail = fail;
        }

        public void Run() => _run();
        public void Fail(Exception ex) => _fail(ex);
    }
}
