using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;

namespace ForestStepDiagnostics;

// No Unity calls or log callbacks on this worker. Bounded memory and disk usage.
public sealed class ServerTrace : IDisposable
{
    private readonly BlockingCollection<string> rows = new(2048);
    private readonly Thread worker;
    private readonly string path;
    private long dropped;
    private volatile string? error;
    private readonly string header;
    private int disposed;
    public long Dropped => Interlocked.Read(ref dropped);
    public string? Error => error;
    public const string Header = "utc,event,update_gap_ms,unity_delta_ms,interval_frames,interval_stalls,interval_max_gap_ms,managed_bytes,gc0_delta,gc1_delta,gc2_delta,cleanup_calls,cleanup_total_ms,cleanup_max_ms,chunk_calls,chunk_total_ms,chunk_max_ms,dropped_rows";

    public ServerTrace(string path, string? header = null)
    {
        this.path = path;
        this.header = header ?? Header;
        worker = new Thread(WriteLoop) { IsBackground = true, Name = "ServerStutterCSV" };
        worker.Start();
    }

    public void Enqueue(string row)
    {
        try
        {
            if (error != null || rows.IsAddingCompleted || !rows.TryAdd(row)) Interlocked.Increment(ref dropped);
        }
        catch (InvalidOperationException) { Interlocked.Increment(ref dropped); }
    }

    private void WriteLoop()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Batch flushes on the worker instead of flushing every diagnostic row.
            bool complete = false;
            while (!complete)
            {
                using (var writer = new StreamWriter(path, true))
                {
                    if (writer.BaseStream.Length == 0) writer.WriteLine(header);
                    var flushClock = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        if (rows.TryTake(out string? row, 1000)) writer.WriteLine(row);
                        if (flushClock.ElapsedMilliseconds >= 1000) { writer.Flush(); flushClock.Restart(); }
                        if (rows.IsCompleted) { complete = true; break; }
                        if (writer.BaseStream.Length >= 16 * 1024 * 1024) break;
                    }
                }
                if (!complete)
                {
                    if (File.Exists(path + ".previous")) File.Delete(path + ".previous");
                    File.Move(path, path + ".previous");
                }
            }
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        rows.CompleteAdding();
        worker.Join(1000); // Never indefinitely hold up shutdown on disk I/O.
    }

    public static string Row(params object[] values) => string.Join(",", Array.ConvertAll(values,
        value => Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")));
    private static string Escape(string value) => value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
        ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
}
