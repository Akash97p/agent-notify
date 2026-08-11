using System.Text;

namespace AgentNotify.Core.Logging;

public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

/// <summary>Minimal thread-safe file logger. Writes to
/// %LOCALAPPDATA%\AgentNotify\logs\agentnotify-YYYYMMDD.log.</summary>
public sealed class FileLogger : IAppLogger, IDisposable
{
    private readonly object _lock = new();
    private readonly string _logsDir;
    private string _currentDate = "";
    private StreamWriter? _writer;

    public FileLogger(string logsDir)
    {
        _logsDir = logsDir;
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message);
        if (exception is not null)
            Write("ERROR", exception.ToString());
    }

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_logsDir);
                var now = DateTime.Now;
                var date = now.ToString("yyyyMMdd");
                if (_writer is null || date != _currentDate)
                {
                    _writer?.Dispose();
                    _currentDate = date;
                    var path = Path.Combine(_logsDir, $"agentnotify-{date}.log");
                    _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
                }
                _writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
