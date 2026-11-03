using System.Diagnostics;
using Avalonia.Controls;

namespace PdfReader.App.Services;

/// <summary>
/// Opt-in diagnostics for lag testing: set PDFREADER_PERFLOG=path\to\log.txt. Records every
/// rendered frame (gaps mean dropped frames or a blocked UI thread) and how long newly visible
/// pages stay blurry. Off by default and costs nothing when disabled.
/// </summary>
public static class PerfLog
{
    private static readonly string? s_path = Environment.GetEnvironmentVariable("PDFREADER_PERFLOG");
    private static readonly Lock s_lock = new();
    private static readonly List<double> s_frameGaps = [];
    private static StreamWriter? s_writer;
    private static TopLevel? s_topLevel;
    private static long s_lastFrame;

    public static bool Enabled => s_path is not null;

    public static void Start(TopLevel topLevel)
    {
        if (s_path is null)
            return;
        s_writer = new StreamWriter(s_path, append: false) { AutoFlush = true };
        s_topLevel = topLevel;
        Write("start");
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private static void OnFrame(TimeSpan _)
    {
        var now = Stopwatch.GetTimestamp();
        if (s_lastFrame != 0)
        {
            var gap = Stopwatch.GetElapsedTime(s_lastFrame, now).TotalMilliseconds;
            lock (s_lock)
                s_frameGaps.Add(gap);
            Write($"f {gap:0.0}");
        }
        s_lastFrame = now;
        s_topLevel?.RequestAnimationFrame(OnFrame);
    }

    /// <summary>Time a visible area was shown without sharp tiles (blank or stretched placeholder).</summary>
    public static void Blurry(double ms)
    {
        if (Enabled)
            Write($"blurry {ms:0}ms");
    }

    public static void Write(string message)
    {
        if (s_writer is null)
            return;
        lock (s_lock)
            s_writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    /// <summary>Writes frame-time percentiles; called on exit.</summary>
    public static void Summary()
    {
        if (s_writer is null)
            return;
        double[] gaps;
        lock (s_lock)
            gaps = s_frameGaps.Order().ToArray();
        if (gaps.Length > 0)
        {
            double P(double q) => gaps[Math.Min(gaps.Length - 1, (int)(gaps.Length * q))];
            Write($"frames {gaps.Length} p50 {P(0.5):0.0}ms p95 {P(0.95):0.0}ms p99 {P(0.99):0.0}ms max {gaps[^1]:0}ms " +
                  $">33ms {gaps.Count(g => g > 33)} >100ms {gaps.Count(g => g > 100)}");
        }
        s_writer.Flush();
    }
}
