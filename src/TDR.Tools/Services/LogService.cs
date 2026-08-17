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
        public string? TrackTag { get; set; }
        public string? VariantTag { get; set; }

        public LogEntry(LogLevel level, string message, string? trackTag = null, string? variantTag = null)
        {
            Timestamp = DateTime.Now;
            Level = level;
            Message = message;
            TrackTag = trackTag;
            VariantTag = variantTag;
        }

        public string TimeText => $"[{Timestamp:HH:mm:ss}]";
        public string FormattedText => $"[{Timestamp:HH:mm:ss}] {Message}";

        public string ForegroundBrush => Level switch
        {
            LogLevel.Error => "#E06C75",    // Calm muted red
            LogLevel.Warning => "#E5C07B",  // Soft amber
            LogLevel.Summary => "#98C379",  // Subtle mint/green
            LogLevel.Debug => "#5C6370",    // Muted grey
            _ => Message.Contains("[GLTF]", StringComparison.OrdinalIgnoreCase) ? "#61AFEF" : // Calm cyan/blue
                 Message.Contains("[OBJ]", StringComparison.OrdinalIgnoreCase) ? "#C678DD" :  // Soft purple
                 Message.Contains("[MTL", StringComparison.OrdinalIgnoreCase) ? "#D19A66" :
                 Message.Contains("[+]") ? "#98C379" : "#ABB2BF"                              // Default calm light grey
        };

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
        private const int MaxHistoryCount = 5000;

        public string? CurrentTrackContext { get; set; }
        public string? CurrentVariantContext { get; set; }

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

        public void Log(LogLevel level, string message, string? trackTag = null, string? variantTag = null)
        {
            if (!IsEnabled(level) || string.IsNullOrWhiteSpace(message)) return;

            var entry = new LogEntry(level, message, trackTag ?? CurrentTrackContext, variantTag ?? CurrentVariantContext);
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
