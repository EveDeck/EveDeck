using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using EveDeck.Models;

namespace EveDeck.Services;

// Linux counterpart of the Windows app's LogService: same public surface, so linked shared code that
// logs compiles unchanged, but it marshals to Avalonia's dispatcher instead of WPF's.
public sealed class LogService
{
    private readonly string _logFolder;
    private readonly object _fileLock = new();

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogService(string logFolder)
    {
        _logFolder = logFolder;
        Directory.CreateDirectory(_logFolder);
    }

    public void Info(string message) => Write("Info", message);
    public void Warn(string message) => Write("Warn", message);
    public void Error(string message) => Write("Error", message);

    private void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_fileLock)
        {
            try { File.AppendAllText(Path.Combine(_logFolder, $"evedeck-{now:yyyyMMdd}.log"), line + Environment.NewLine); }
            catch { } // logging must never take the app down
        }
        var entry = new LogEntry { Timestamp = now, Level = level, Message = message };
        if (Dispatcher.UIThread.CheckAccess()) Entries.Add(entry);
        else Dispatcher.UIThread.Post(() => Entries.Add(entry));
    }
}
