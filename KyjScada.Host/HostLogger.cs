using KyjScada.Contracts;

namespace KyjScada.Host;

/// <summary>
/// 宿主日志。所有模块日志统一汇聚,带模块前缀与时间戳。
/// 独立调试时可只输出某个模块的日志。
/// </summary>
public sealed class HostLogger
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _lock = new();
    private readonly string? _onlyModuleId;
    private readonly Action<LogEntry>? _sink;

    public HostLogger (string? onlyModuleId = null, Action<LogEntry>? sink = null)
    {
        _onlyModuleId = onlyModuleId;
        _sink = sink;
    }

    public void Info (string message) => Write ("INFO", null, message, null);

    public void Warn (string message) => Write ("WARN", null, message, null);

    public void LogError (string message, Exception? ex = null) => Write ("ERROR", null, message, ex);

    public IModuleLogger ForModule (string moduleId) => new ScopedLogger (this, moduleId);

    internal void Write (string level, string? moduleId, string message, Exception? ex)
    {
        var entry = new LogEntry (DateTime.Now, level, moduleId, message, ex);

        lock (_lock)
        {
            _entries.Add (entry);
            if (_entries.Count > 5000) _entries.RemoveRange (0, 1000);
        }

        if (_onlyModuleId != null && moduleId != null && moduleId != _onlyModuleId) return;

        _sink?.Invoke (entry);
    }

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock) return _entries.ToList ();
        }
    }

    private sealed class ScopedLogger : IModuleLogger
    {
        private readonly HostLogger _host;
        private readonly string _moduleId;

        public ScopedLogger (HostLogger host, string moduleId)
        {
            _host = host;
            _moduleId = moduleId;
        }

        public void Info (string message) => _host.Write ("INFO", _moduleId, message, null);

        public void Warn (string message) => _host.Write ("WARN", _moduleId, message, null);

        public void LogError (string message, Exception? ex = null) => _host.Write ("ERROR", _moduleId, message, ex);
    }
}

public sealed record LogEntry (DateTime Time, string Level, string? ModuleId, string Message, Exception? Exception)
{
    public override string ToString ()
    {
        var mod = ModuleId == null ? "host" : ModuleId;
        return $"{Time:HH:mm:ss.fff} [{Level,-5}] [{mod}] {Message}";
    }
}
