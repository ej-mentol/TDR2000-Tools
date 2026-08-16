using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TDR.Tools.Services
{
    public enum LogLevel
    {
        Info,
        Warning,
        Error,
        Summary,
        Debug
    }

    public sealed class LogEntry
    {
        public DateTime Timestamp { get; }
        public LogLevel Level { get; }
        public string Message { get; }

        public LogEntry(LogLevel level, string message)
        {
            Timestamp = DateTime.Now;
            Level = level;
            Message = message;
        }

        public string FormattedText => $"[{Timestamp:HH:mm:ss}] {Message}";

        public override string ToString() => FormattedText;
    }

    /// <summary>
    /// Centralized, thread-safe logging service for TDR Tools.
    /// Decouples export pipelines, parsers, and background tasks from Avalonia UI elements.
    /// </summary>
    public sealed class LogService
    {
        private static readonly Lazy<LogService> _instance = new(() => new LogService());
        public static LogService Instance => _instance.Value;

        private readonly object _lock = new();
        private readonly Queue<LogEntry> _entries = new();
        private const int MaxHistoryCount = 2500;

        private bool _isDebugEnabled;
        public bool IsDebugEnabled
        {
            get => _isDebugEnabled || AppSettings.Load().DebugMode;
            set => _isDebugEnabled = value;
        }

        public bool IsEnabled(LogLevel level)
        {
            if (level == LogLevel.Debug && !IsDebugEnabled) return false;
            return true;
        }

        public event Action<LogEntry>? OnLogAdded;
        public event Action? OnLogCleared;

        public void Log(LogLevel level, string message)
        {
            if (!IsEnabled(level) || string.IsNullOrWhiteSpace(message)) return;

            var entry = new LogEntry(level, message);
            lock (_lock)
            {
                _entries.Enqueue(entry);
                if (_entries.Count > MaxHistoryCount)
                {
                    _entries.Dequeue();
                }
            }

            OnLogAdded?.Invoke(entry);
        }

        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warning, message);
        public void Error(string message) => Log(LogLevel.Error, message);
        public void Summary(string message) => Log(LogLevel.Summary, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
            OnLogCleared?.Invoke();
        }

        public IReadOnlyList<LogEntry> GetAllEntries()
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }

        public string GetFilteredText(Func<LogEntry, bool>? predicate = null)
        {
            lock (_lock)
            {
                var query = predicate != null ? _entries.Where(predicate) : _entries;
                var sb = new StringBuilder();
                foreach (var entry in query)
                {
                    sb.AppendLine(entry.FormattedText);
                }
                return sb.ToString().TrimEnd();
            }
        }

        public string GetAllText() => GetFilteredText(null);

        public string GetWarningsText() =>
            GetFilteredText(e => e.Level == LogLevel.Warning ||
                                 e.Message.Contains("[!]") ||
                                 e.Message.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ||
                                 e.Message.Contains("warning", StringComparison.OrdinalIgnoreCase));

        public string GetErrorsText() =>
            GetFilteredText(e => e.Level == LogLevel.Error ||
                                 e.Message.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                                 e.Message.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                                 e.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));

        public string GetSummariesText() =>
            GetFilteredText(e => e.Level == LogLevel.Summary ||
                                 e.Message.Contains("Exported:", StringComparison.OrdinalIgnoreCase) ||
                                 e.Message.Contains("EXPORT SUMMARY", StringComparison.OrdinalIgnoreCase) ||
                                 e.Message.Contains("Summary:", StringComparison.OrdinalIgnoreCase) ||
                                 e.Message.TrimStart().StartsWith("• Layers") ||
                                 e.Message.TrimStart().StartsWith("• Props") ||
                                 e.Message.TrimStart().StartsWith("• Spawns"));
    }
}
