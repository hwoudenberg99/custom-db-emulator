namespace DbEmulator.App;

internal sealed record LogEntry(long Id, DateTimeOffset Time, string Level, string Message);

/// <summary>The activity log shown in the app: the most recent entries, kept in memory.</summary>
internal sealed class MemoryLog : IEmulatorLog
{
    private const int Capacity = 1000;
    private readonly object _gate = new();
    private readonly LinkedList<LogEntry> _entries = new();
    private long _nextId = 1;

    public void Info(string message) => Add("info", message);

    public void Warn(string message) => Add("warn", message);

    public void Error(string message) => Add("error", message);

    public void Request(string message) => Add("request", message);

    public void Success(string message) => Add("success", message);

    public IReadOnlyList<LogEntry> After(long id, int max = 500)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Id > id).TakeLast(max).ToList();
        }
    }

    private void Add(string level, string message)
    {
        lock (_gate)
        {
            _entries.AddLast(new LogEntry(_nextId++, DateTimeOffset.Now, level, message));
            while (_entries.Count > Capacity)
            {
                _entries.RemoveFirst();
            }
        }
    }
}
