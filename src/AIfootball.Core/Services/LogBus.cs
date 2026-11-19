using System.Collections.ObjectModel;
using System.Text;
using AIfootball.Core.Models;

namespace AIfootball.Core.Services;

/// <summary>应用日志总线：内存环形缓冲 + 分文件落盘，UI 与文件共享同一份事实。</summary>
public sealed class LogBus
{
    private const int MaxBufferedEntries = 1500;
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _buffer = new(MaxBufferedEntries + 1);
    private readonly string _logDirectory;
    private readonly string _logFile;

    public LogBus(string workspaceRoot)
    {
        _logDirectory = Path.Combine(workspaceRoot, "logs");
        _logFile = Path.Combine(_logDirectory, $"aifootball-{DateTime.Now:yyyyMMdd}.log");
    }

    public ObservableCollection<LogEntry> Recent { get; } = [];

    /// <summary>供非 UI 线程写入；集合更新会切到 UI 调度器。</summary>
    public event Action<LogEntry>? EntryWritten;

    public void Write(LogLevel level, string message, string source = "app")
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message, source);
        lock (_gate)
        {
            _buffer.Enqueue(entry);
            while (_buffer.Count > MaxBufferedEntries) _buffer.Dequeue();
        }
        EntryWritten?.Invoke(entry);
        AppendToFile(entry);
    }

    public void Info(string message, string source = "app") => Write(LogLevel.Info, message, source);
    public void Success(string message, string source = "app") => Write(LogLevel.Success, message, source);
    public void Warn(string message, string source = "app") => Write(LogLevel.Warning, message, source);
    public void Error(string message, string source = "app") => Write(LogLevel.Error, message, source);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return [.. _buffer];
    }

    public string LogFilePath => _logFile;

    private void AppendToFile(LogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var line = new StringBuilder()
                .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(entry.Level.ToString().ToUpperInvariant()).Append("] ")
                .Append('[').Append(entry.Source).Append("] ")
                .Append(entry.Message)
                .Append(Environment.NewLine)
                .ToString();
            File.AppendAllText(_logFile, line, Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
