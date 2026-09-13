using System.Collections.ObjectModel;
using System.IO;
using EveDeck.Models;

namespace EveDeck.Services;

public sealed class LogService
{
    private readonly string _logFolder;
    private string _logPath;
    private DateOnly _logDate;

    // Serialises the file append and the _logDate/_logPath roll against concurrent background writers.
    private readonly object _fileLock = new();

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

        // Entries is bound to the Log tab, and WPF forbids mutating a collection view's source from
        // any thread but the dispatcher's. Background callers are the NORM here -- the WGC capture
        // threads, both log watchers, ESI polling -- so an unmarshalled Insert throws
        // "This type of CollectionView does not support changes to its SourceCollection from a
        // thread different from the Dispatcher thread" as soon as the Log tab has been viewed once
        // (before that there is no view attached and it silently succeeds, which is why it presents
        // as intermittent).
        //
        // That throw did not stay inside logging. WgcTileCaptureSession.OnFrame caught it and
        // recorded it as a *frame* fault ("frame: <the cross-thread message>"), so enough log lines
        // from the capture thread would rebuild the tile or demote it to DWM for the rest of the
        // session -- 32 such faults in one week of live logs. Logging must never be able to fault
        // its caller.
        //
        // BeginInvoke, not Invoke: a capture thread must never block on the UI thread.
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) AddEntry(entry);
        else app.Dispatcher.BeginInvoke(() => AddEntry(entry));

        // _logDate/_logPath are shared mutable state and File.AppendAllText is not safe against
        // concurrent writers to the same path; both are reached from every one of those threads.
        try
        {
            lock (_fileLock)
            {
                RollIfDateChanged();
                File.AppendAllText(_logPath, entry.Display + Environment.NewLine);
            }
        }
        catch
        {
            // Disk full, file locked by an external reader, folder removed mid-session: losing a log
            // line is acceptable, propagating out of Log.Error() into the caller's fault path is not.
        }
    }

    private void AddEntry(LogEntry entry)
    {
        Entries.Insert(0, entry);
        while (Entries.Count > 500)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }
    }
}
