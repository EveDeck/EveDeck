using System.Collections.ObjectModel;
using System.IO;
using EveDeck.Models;

namespace EveDeck.Services;

public sealed class LogService
{
    private readonly string _logFolder;
    private string _logPath;
    private DateOnly _logDate;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogService(string logFolder)
    {
        _logFolder = logFolder;
        Directory.CreateDirectory(_logFolder);
        _logDate = DateOnly.FromDateTime(DateTime.Now);
        _logPath = PathFor(_logDate);
    }

    private string PathFor(DateOnly date) => Path.Combine(_logFolder, $"evedeck-{date:yyyyMMdd}.log");

    // The dated filename used to be resolved once, in the constructor, so a session left running past
    // midnight kept appending to the previous day's file indefinitely -- one live install was still
    // writing to evedeck-20260811.log late on the 12th. EveDeck is a sit-in-the-tray-for-days app, so
    // that is the normal case, not an edge case. Re-check the date on every write instead.
    private void RollIfDateChanged()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _logDate) return;
        _logDate = today;
        _logPath = PathFor(today);
    }

    public void Info(string message) => Write("Info", message);
    public void Warn(string message) => Write("Warn", message);
    public void Error(string message) => Write("Error", message);

    private void Write(string level, string message)
    {
        var entry = new LogEntry { Level = level, Message = message };
        Entries.Insert(0, entry);
        while (Entries.Count > 500)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        RollIfDateChanged();
        File.AppendAllText(_logPath, entry.Display + Environment.NewLine);
    }
}
