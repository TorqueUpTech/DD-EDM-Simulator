using System.IO;

namespace DynoCommandPC.Logging;

/// <summary>
/// Appends timestamped comms-lifecycle events (connect, comms up/lost/restored,
/// disconnect) to an auto-created log file. A fresh file with a date-time name is
/// started on each connection, under a "logs" folder beside the executable.
/// Thread-safe — written from both the UI and TX threads.
/// </summary>
public sealed class CommsLogger : IDisposable
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "logs");

    private readonly object _lock = new();
    private StreamWriter? _writer;

    public bool IsOpen { get { lock (_lock) { return _writer != null; } } }
    public string? FilePath { get; private set; }

    /// <summary>Open a new timestamped log file (closing any previous one).</summary>
    public void Start()
    {
        lock (_lock)
        {
            StopNoLock();
            try
            {
                Directory.CreateDirectory(Dir);
                FilePath = Path.Combine(Dir, $"dyno-comms-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                _writer = new StreamWriter(FilePath, append: true) { AutoFlush = true };
                _writer.WriteLine($"# Dyno comms log — started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch { _writer = null; FilePath = null; }
        }
    }

    public void Log(string message)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            try { _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}"); }
            catch { /* best effort */ }
        }
    }

    public void Stop()
    {
        lock (_lock) { StopNoLock(); }
    }

    private void StopNoLock()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
    }

    public void Dispose() => Stop();
}
