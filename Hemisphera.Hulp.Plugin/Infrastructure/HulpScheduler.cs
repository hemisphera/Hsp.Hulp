using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ReaSharp;

namespace Hemisphera.Hulp.Plugin.Infrastructure;

/// <summary>
/// Runs all of the plugin's REAPER work (actions, polling, looper, await continuations) one pass at a time, driven
/// either by REAPER's main thread or by a dedicated background thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safe mode</b> (<see cref="SafeMode"/> = true): passes run in a REAPER <c>timer</c> callback on REAPER's main
/// thread, roughly 30 times per second. REAPER's API is meant to be called from the main thread only.
/// </para>
/// <para>
/// <b>Fast mode</b> (<see cref="SafeMode"/> = false): passes run on a dedicated background thread every
/// <see cref="FastModeInterval"/>. This is how the plugin used to work (finer timing, but the REAPER API is called
/// off the main thread).
/// </para>
/// <para>
/// In both modes passes never overlap, and the scheduler is the <see cref="SynchronizationContext"/> of the thread
/// running them, so an <c>await</c> resumes in a later pass. The mode can be switched at any time.
/// </para>
/// </remarks>
public sealed class HulpScheduler : SynchronizationContext
{
  private static HulpScheduler? _instance;

  private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
  private readonly Lock _passLock = new();
  private readonly Lock _modeLock = new();
  private ILogger? _logger;
  private string? _lastTickError;
  private bool _timerRegistered;
  private volatile bool _safeMode = true;
  private int _fastModeGeneration;

  /// <summary>Raised once per pass, on the thread running the pass.</summary>
  public event Action? Tick;

  /// <summary>True: run on REAPER's main thread. False: run on a background thread at <see cref="FastModeInterval"/>.</summary>
  public bool SafeMode => _safeMode;

  public TimeSpan FastModeInterval { get; private set; } = TimeSpan.FromMilliseconds(5);


  private HulpScheduler()
  {
  }


  /// <summary>Creates the scheduler. Must be called on REAPER's main thread (i.e. from <c>ReaperPluginEntry</c>).</summary>
  public static HulpScheduler Create()
  {
    _instance = new HulpScheduler();
    return _instance;
  }

  public void SetLogger(ILogger logger) => _logger = logger;

  /// <summary>Registers the REAPER timer and starts running passes in the given mode.</summary>
  public void Start(bool safeMode, TimeSpan fastModeInterval)
  {
    if (!_timerRegistered)
    {
      RegisterTimer("timer");
      _timerRegistered = true;
    }

    SetMode(safeMode, fastModeInterval);
    _logger?.LogInformation(_safeMode
        ? "Scheduler started in safe mode (REAPER main thread)."
        : "Scheduler started in fast mode (background thread, {interval} ms).",
      FastModeInterval.TotalMilliseconds);
  }

  public void Stop()
  {
    lock (_modeLock)
    {
      _fastModeGeneration++; // ends a running background thread
      _safeMode = true;
    }

    if (_timerRegistered)
    {
      RegisterTimer("-timer");
      _timerRegistered = false;
    }
  }

  /// <summary>Switches between safe mode and fast mode. Safe to call from any thread.</summary>
  public void SetMode(bool safeMode, TimeSpan? fastModeInterval = null)
  {
    lock (_modeLock)
    {
      if (fastModeInterval is { } interval && interval > TimeSpan.Zero)
        FastModeInterval = interval;

      var changed = _safeMode != safeMode;
      _safeMode = safeMode;

      if (safeMode)
      {
        _fastModeGeneration++; // ends a running background thread
      }
      else if (changed)
      {
        // The previous background thread (if any) notices the new generation and ends after its current pass.
        var generation = ++_fastModeGeneration;
        new Thread(() => FastModeLoop(generation))
        {
          IsBackground = true,
          Name = "Hulp scheduler",
          Priority = ThreadPriority.AboveNormal
        }.Start();
      }

      if (changed)
      {
        _logger?.LogWarning(safeMode
            ? "Safe mode ON: running on REAPER's main thread (~30 Hz)."
            : "Safe mode OFF: running on a background thread every {interval} ms.",
          FastModeInterval.TotalMilliseconds);
      }
    }
  }

  private void FastModeLoop(int generation)
  {
    SetSynchronizationContext(this);
    while (Volatile.Read(ref _fastModeGeneration) == generation)
    {
      RunPass();
      Thread.Sleep(FastModeInterval);
    }
  }

  private static unsafe void RegisterTimer(string name)
  {
    var callback = (nint)(delegate* unmanaged[Cdecl]<void>)(&TimerCallback);
    var namePtr = Marshal.StringToHGlobalAnsi(name);
    try
    {
      Reaper.Register(namePtr, callback);
    }
    finally
    {
      Marshal.FreeHGlobal(namePtr);
    }
  }

  [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
  private static void TimerCallback()
  {
    // Exceptions must never escape into REAPER: that would take down the host.
    try
    {
      var instance = _instance;
      if (instance is { _safeMode: true })
      {
        if (Current != instance) SetSynchronizationContext(instance);
        instance.RunPass();
      }
    }
    catch (Exception ex)
    {
      try
      {
        ReaperConsoleLogger.WriteLog("Hulp: unhandled exception in timer: " + ex.Message);
      }
      catch
      {
        // nothing left to do
      }
    }
  }

  private void RunPass()
  {
    // While switching modes, the old and the new driver may briefly both be active: never run two passes at once.
    if (!_passLock.TryEnter()) return;
    try
    {
      ProcessQueue();
      RaiseTick();
    }
    finally
    {
      _passLock.Exit();
    }
  }

  private void ProcessQueue()
  {
    // Only run what was queued before this pass: work queued while processing (e.g. an await continuation that
    // immediately awaits again) runs in the next pass instead of spinning here.
    var count = _queue.Count;
    for (var i = 0; i < count && _queue.TryDequeue(out var item); i++)
    {
      try
      {
        item.Callback(item.State);
      }
      catch (Exception ex)
      {
        _logger?.LogError(ex, "Error in scheduled work item: {message}", ex.Message);
      }
    }
  }

  private void RaiseTick()
  {
    var handlers = Tick?.GetInvocationList();
    if (handlers == null) return;

    // Each handler separately, so that one failing subscriber doesn't starve the others.
    foreach (var handler in handlers)
    {
      try
      {
        ((Action)handler)();
      }
      catch (Exception ex)
      {
        // Log once per distinct error instead of on every pass.
        var source = handler.Target?.GetType().Name;
        var key = source + ": " + ex.Message;
        if (key == _lastTickError) continue;
        _lastTickError = key;
        _logger?.LogError(ex, "Error in scheduler tick ({source}): {message}", source, ex.Message);
      }
    }
  }

  public override void Post(SendOrPostCallback d, object? state)
  {
    _queue.Enqueue((d, state));
  }

  public override void Send(SendOrPostCallback d, object? state)
  {
    if (Current == this)
    {
      d(state);
      return;
    }

    using var done = new ManualResetEventSlim(false);
    Exception? error = null;
    _queue.Enqueue((s =>
    {
      try
      {
        d(s);
      }
      catch (Exception ex)
      {
        error = ex;
      }
      finally
      {
        done.Set();
      }
    }, state));
    done.Wait();
    if (error != null) throw error;
  }

  public override SynchronizationContext CreateCopy() => this;

  /// <summary>Queues <paramref name="action"/> to run in the next pass.</summary>
  public void Post(Action action) => Post(_ => action(), null);

  /// <summary>
  /// Runs <paramref name="func"/> in the next pass and completes when the returned task completes. Continuations
  /// inside <paramref name="func"/> also run in scheduler passes.
  /// </summary>
  public Task InvokeAsync(Func<Task> func)
  {
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Post(async () =>
    {
      try
      {
        await func();
        tcs.SetResult();
      }
      catch (Exception ex)
      {
        tcs.SetException(ex);
      }
    });
    return tcs.Task;
  }

  /// <summary>Observes a fire-and-forget task so that its failure is logged instead of lost.</summary>
  public static void Forget(Task task, ILogger logger, [CallerArgumentExpression(nameof(task))] string? what = null)
  {
    if (task.IsCompletedSuccessfully) return;
    task.ContinueWith(
      t => logger.LogError(t.Exception, "Background operation '{what}' failed: {message}", what, t.Exception?.GetBaseException().Message),
      CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);
  }
}
