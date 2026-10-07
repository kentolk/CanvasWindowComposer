using System;
using System.Collections.Generic;
using System.Threading;

namespace CanvasDesktop;

/// <summary>
/// Offloads the SetWindowPos phase of reprojection to a dedicated thread so the
/// UI thread isn't blocked waiting on slow-drawing windows to ack their moves.
///
/// Single-producer / single-consumer: UI thread builds a batch and calls
/// Schedule; the worker thread consumes the latest scheduled batch via a
/// volatile reference swap and applies it with BatchMove(sync). Newer batches
/// replace older ones that haven't been consumed yet, so the worker always
/// converges on the most recent canvas state.
/// </summary>
internal sealed class ProjectionWorker : IDisposable
{
    private readonly IWindowApi _win32;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _signal = new(false);
    // Held while the worker is inside _win32.BatchMove. ClearPending grabs
    // this briefly after cancelling so the follow-up sync BatchMove can't
    // race the in-flight worker batch on the same HWNDs.
    private readonly object _processLock = new();
    // Doubles as the shutdown signal AND the in-flight-batch canceller.
    // ClearPending cancels + replaces with a fresh CTS so the next batch has
    // a live token; Dispose cancels without replacing.
    private CancellationTokenSource _cts = new();
    private volatile bool _disposed;
    private Job? _pending;

    // Monotonic counters, not a flag: the caller needs to know whether every
    // batch it handed over actually reached the windows. _applied only advances
    // when a batch runs to completion uncancelled, so _scheduled != _applied
    // means at least one batch was dropped or cut short and the caller's idea of
    // where the windows are is ahead of the truth.
    private long _scheduled;
    private long _applied;

    private sealed class Job
    {
        public required List<BatchMoveItem> Items;
        public long Seq;
        public bool IsTransient;
        public bool IsAsync;
    }

    public ProjectionWorker(IWindowApi win32)
    {
        _win32 = win32;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Projection"
        };
        _thread.Start();
    }

    /// <summary>UI thread: hand off the latest batch. Overwrites any earlier pending batch.</summary>
    public void Schedule(
        List<BatchMoveItem> items,
        bool isAsync,
        bool isTransient)
    {
        long seq = Interlocked.Increment(ref _scheduled);
        Volatile.Write(ref _pending, new Job { Items = items, Seq = seq, IsAsync = isAsync, IsTransient = isTransient });
        _signal.Set();
    }

    /// <summary>
    /// Drop any queued batch and cancel the in-flight one if any. The
    /// in-flight BatchMove exits between items instead of finalizing -
    /// callers that follow up with their own sync BatchMove get to run
    /// without waiting for the full batch to complete.
    /// </summary>
    /// <returns>
    /// True if any scheduled batch never made it to the windows in full — it was
    /// still queued, or it was cut short mid-flight. The caller has already
    /// recorded those positions as applied, so on true it must re-issue the moves
    /// rather than trust that bookkeeping.
    /// </returns>
    public bool ClearPending()
    {
        var prev = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        prev.Cancel();

        // Deliberately not disposed. The worker is still holding this token —
        // parked in _signal.Wait(token), or inside BatchMove — and a token whose
        // source has been disposed throws ObjectDisposedException from
        // ct.WaitHandle and from the registration Wait sets up. That lands on the
        // worker thread, where nothing catches it, and takes the process with it.
        // Cancelling is what matters here; the source is small and collectable.
        lock (_processLock)
        {
            Interlocked.Exchange(ref _pending, null);
            // Inside the lock: the worker either hasn't started its batch (and
            // now never will — the job is gone and its token is cancelled) or has
            // already left BatchMove, so both counters are settled.
            return Volatile.Read(ref _scheduled) != Volatile.Read(ref _applied);
        }
    }

    private void Loop()
    {
        while (!_disposed)
        {
            CancellationTokenSource cts = _cts;
            try
            {
                _signal.Wait(cts.Token);
            }
            catch (OperationCanceledException)
            {
                continue; // _cts was swapped or disposed — re-read next iteration.
            }
            _signal.Reset();
            if (_disposed) break;
            lock (_processLock)
            {
                Job? job = Interlocked.Exchange(ref _pending, null);
                if (job != null && !cts.IsCancellationRequested && !_disposed)
                {
                    _win32.BatchMove(job.Items, isAsync: job.IsAsync, isTransient: job.IsTransient, ct: cts.Token);

                    // Only a batch that ran to the end counts as applied. A
                    // cancelled one stops between items, so some windows moved
                    // and some did not — indistinguishable from none, as far as
                    // the caller's bookkeeping goes.
                    if (!cts.IsCancellationRequested)
                        Volatile.Write(ref _applied, job.Seq);
                }
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        _signal.Set(); // in case the worker is parked in Wait with no pending job

        // Reclaim the primitives only once the worker is provably gone. It can
        // still be inside BatchMove, which blocks on cross-process SetWindowPos
        // round-trips and has no hard upper bound — disposing _signal or _cts
        // underneath it throws ObjectDisposedException on a background thread,
        // and that terminates the process on the way out of a clean exit.
        if (!_thread.Join(TimeSpan.FromSeconds(1)))
            return;

        _signal.Dispose();
        _cts.Dispose();
    }

}
