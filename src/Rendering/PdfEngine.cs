using PdfReader.Rendering.Native;

namespace PdfReader.Rendering;

/// <summary>Lower value = runs first.</summary>
public enum WorkPriority
{
    /// <summary>Opening documents, printing, anything the user is waiting on.</summary>
    Immediate = 0,
    /// <summary>Tiles and previews currently on screen.</summary>
    Visible = 1,
    /// <summary>Text hit-tests, selection, search.</summary>
    Interactive = 2,
    /// <summary>Tiles just outside the viewport.</summary>
    Prefetch = 3,
    /// <summary>Thumbnails, security scans, read-aloud text.</summary>
    Background = 4,
}

/// <summary>
/// Owns the single thread that is allowed to talk to PDFium (PDFium is not thread-safe).
/// Work is queued by priority; cancelled items are dropped before they start, and
/// long renders check their token through PDFium's pause callback.
/// </summary>
public sealed class PdfEngine
{
    private static readonly Lazy<PdfEngine> s_shared = new(() => new PdfEngine(), LazyThreadSafetyMode.ExecutionAndPublication);
    public static PdfEngine Shared => s_shared.Value;

    [ThreadStatic] private static CancellationToken t_currentToken;

    private readonly PriorityQueue<WorkItem, (int Priority, long Sequence)> _queue = new();
    private readonly object _lock = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _sequence;

    private PdfEngine()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PDFium", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>The token of the work item currently executing on the engine thread.</summary>
    internal static CancellationToken CurrentToken => t_currentToken;

    internal bool IsEngineThread => Thread.CurrentThread == _thread;

    public Task<T> RunAsync<T>(Func<T> work, WorkPriority priority = WorkPriority.Interactive, CancellationToken cancellationToken = default)
    {
        var item = new WorkItem<T>(work, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            item.Cancel();
            return item.Task;
        }

        lock (_lock)
        {
            _queue.Enqueue(item, ((int)priority, _sequence++));
            Monitor.Pulse(_lock);
        }
        return item.Task;
    }

    public Task RunAsync(Action work, WorkPriority priority = WorkPriority.Interactive, CancellationToken cancellationToken = default)
        => RunAsync<bool>(() => { work(); return true; }, priority, cancellationToken);

    private void Run()
    {
        try
        {
            Pdfium.FPDF_InitLibrary();
            _initialized.TrySetResult();
        }
        catch (Exception ex)
        {
            // Missing/invalid pdfium.dll: fail every request instead of crashing the app.
            _initialized.TrySetException(ex);
        }

        while (true)
        {
            WorkItem item;
            lock (_lock)
            {
                while (_queue.Count == 0)
                    Monitor.Wait(_lock);
                item = _queue.Dequeue();
            }

            if (_initialized.Task.IsFaulted)
            {
                item.Fail(new PdfException(PdfError.EngineUnavailable, "PDFium could not be loaded.", _initialized.Task.Exception?.InnerException));
                continue;
            }

            t_currentToken = item.Token;
            try { item.Execute(); }
            finally { t_currentToken = default; }
        }
    }

    private abstract class WorkItem
    {
        public abstract CancellationToken Token { get; }
        public abstract void Execute();
        public abstract void Fail(Exception ex);
    }

    private sealed class WorkItem<T>(Func<T> work, CancellationToken token) : WorkItem
    {
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _tcs.Task;
        public override CancellationToken Token => token;

        public void Cancel() => _tcs.TrySetCanceled(token);

        public override void Fail(Exception ex) => _tcs.TrySetException(ex);

        public override void Execute()
        {
            if (token.IsCancellationRequested)
            {
                _tcs.TrySetCanceled(token);
                return;
            }

            try
            {
                _tcs.TrySetResult(work());
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _tcs.TrySetCanceled(token);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }
    }
}
