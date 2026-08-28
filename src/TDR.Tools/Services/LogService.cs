using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

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
            _ => Message.Contains("═") || Message.Contains("──") || Message.StartsWith("---") ? "#5C6370" : // Muted separator grey (calm divider)
                 Message.Contains("[GLTF]", StringComparison.OrdinalIgnoreCase) ? "#61AFEF" : // Calm cyan/blue
                 Message.Contains("[OBJ]", StringComparison.OrdinalIgnoreCase) ? "#C678DD" :  // Soft purple
                 Message.Contains("[MTL", StringComparison.OrdinalIgnoreCase) ? "#D19A66" :   // Soft copper/orange
                 Message.Contains("[+]") ? "#98C379" :                                        // Subtle mint/green
                 Message.Contains("[!]") ? "#E5C07B" : "#ABB2BF"                              // Soft amber or default calm light grey
        };

        public override string ToString() => FormattedText;
    }

    /// <summary>
    /// Centralized, dual-layer high-performance logging service for TDR Tools.
    /// Layer 1 (Disk): Synchronous instant AutoFlush file persistence (guaranteed crash safety).
    /// Layer 2 (UI): 50ms lock-free micro-batching with immediate bypass on Warnings/Errors.
    /// </summary>
    public sealed class LogService : IDisposable
    {
        private static readonly Lazy<LogService> _instance = new(() => new LogService());
        public static LogService Instance => _instance.Value;

        private readonly object _lock = new();
        private readonly Queue<LogEntry> _entries = new();
        private readonly ConcurrentQueue<LogEntry> _pendingUiQueue = new();
        private readonly Timer _batchTimer;
        private StreamWriter? _diskLogWriter;
        private readonly string _logFilePath;

        private const int MaxHistoryCount = 5000;
        private const int BatchIntervalMs = 50;
        private const int MaxBatchDrainSize = 400;

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
        public event Action<IReadOnlyList<LogEntry>>? OnLogBatchAdded;
        public event Action? OnLogCleared;

        public LogService()
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TDR2000-Tools",
                    "logs"
                );
                Directory.CreateDirectory(logDir);
                _logFilePath = Path.Combine(logDir, "session.log");

                var fileStream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _diskLogWriter = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = true };
                _diskLogWriter.WriteLine($"\n--- Session Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
            }
            catch
            {
                _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session.log");
            }

            _batchTimer = new Timer(OnBatchTimerTick, null, BatchIntervalMs, BatchIntervalMs);
        }

        public void Log(LogLevel level, string message, string? trackTag = null, string? variantTag = null)
        {
            if (!IsEnabled(level) || string.IsNullOrWhiteSpace(message)) return;

            var entry = new LogEntry(level, message, trackTag ?? CurrentTrackContext, variantTag ?? CurrentVariantContext);

            // 1. Synchronous, instant disk write with AutoFlush (crash safe!)
            try
            {
                lock (_lock)
                {
                    _diskLogWriter?.WriteLine(entry.FormattedText);

                    _entries.Enqueue(entry);
                    if (_entries.Count > MaxHistoryCount)
                    {
                        _entries.Dequeue();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogService] Disk write error: {ex.Message}");
            }

            // 2. Queue for UI micro-batching
            _pendingUiQueue.Enqueue(entry);
            OnLogAdded?.Invoke(entry);

            // 3. If Warning or Error, immediately flush UI batch so crash context appears without 50ms delay
            if (level == LogLevel.Error || level == LogLevel.Warning)
            {
                FlushImmediate();
            }
        }

        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warning, message);
        public void Error(string message) => Log(LogLevel.Error, message);
        public void Summary(string message) => Log(LogLevel.Summary, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);
        public void LogDebug(string message) => Log(LogLevel.Debug, message);

        private void OnBatchTimerTick(object? state)
        {
            DrainAndDispatchUiBatch();
        }

        private void DrainAndDispatchUiBatch()
        {
            if (_pendingUiQueue.IsEmpty) return;

            var batch = new List<LogEntry>();
            while (batch.Count < MaxBatchDrainSize && _pendingUiQueue.TryDequeue(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count > 0)
            {
                OnLogBatchAdded?.Invoke(batch);
            }
        }

        public void FlushImmediate()
        {
            try
            {
                lock (_lock)
                {
                    _diskLogWriter?.Flush();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogService] Flush error: {ex.Message}");
            }

            var batch = new List<LogEntry>();
            while (_pendingUiQueue.TryDequeue(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count > 0)
            {
                OnLogBatchAdded?.Invoke(batch);
            }
        }

        public void FatalCrash(object? exceptionObj)
        {
            DateTime now = DateTime.Now;
            string crashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TDR2000-Tools", "logs", "crashes");
            string crashFileName = $"crash-{now:yyyy-MM-dd_HH-mm-ss}.log";

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine($"TDR2000 Tools — FATAL UNHANDLED CRASH REPORT");
            sb.AppendLine($"Timestamp: {now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
            sb.AppendLine($"Runtime: .NET {Environment.Version}");
            sb.AppendLine("================================================================================");
            sb.AppendLine("EXCEPTION DETAILS:");
            if (exceptionObj is Exception exObj)
            {
                sb.AppendLine($"Type: {exObj.GetType().FullName}");
                sb.AppendLine($"Message: {exObj.Message}");
                sb.AppendLine($"Source: {exObj.Source}");
                sb.AppendLine("Stack Trace:");
                sb.AppendLine(exObj.StackTrace ?? "(No stack trace available)");

                Exception? inner = exObj.InnerException;
                int innerIdx = 1;
                while (inner != null)
                {
                    sb.AppendLine($"--- Inner Exception #{innerIdx} ---");
                    sb.AppendLine($"Type: {inner.GetType().FullName}");
                    sb.AppendLine($"Message: {inner.Message}");
                    sb.AppendLine($"Stack Trace:\n{inner.StackTrace}");
                    inner = inner.InnerException;
                    innerIdx++;
                }
            }
            else
            {
                sb.AppendLine(exceptionObj?.ToString() ?? "Unknown null exception object");
            }

            sb.AppendLine("================================================================================");
            sb.AppendLine("RECENT LOG HISTORY (Last 150 lines):");
            lock (_lock)
            {
                var recent = _entries.TakeLast(150);
                foreach (var entry in recent)
                {
                    sb.AppendLine(entry.FormattedText);
                }
            }
            sb.AppendLine("================================================================================");

            string report = sb.ToString();

            // 1. Write dedicated crash log file
            try
            {
                Directory.CreateDirectory(crashDir);
                string crashFilePath = Path.Combine(crashDir, crashFileName);
                File.WriteAllText(crashFilePath, report, Encoding.UTF8);

                // Also maintain daily alias crash-yyyy-MM-dd.log
                string dailyCrashPath = Path.Combine(crashDir, $"crash-{now:yyyy-MM-dd}.log");
                File.AppendAllText(dailyCrashPath, "\n\n" + report, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogService] Crash report file write error: {ex.Message}");
            }

            // 2. Append to main session.log
            try
            {
                lock (_lock)
                {
                    _diskLogWriter?.WriteLine("\n" + report);
                    _diskLogWriter?.Flush();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogService] Session append error: {ex.Message}");
            }

            Log(LogLevel.Error, $"[CRITICAL FATAL CRASH] Saved crash report: '{crashFileName}'");
            FlushImmediate();
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
            while (_pendingUiQueue.TryDequeue(out _)) { }
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

        public int GetErrorCount(DateTime? since = null)
        {
            lock (_lock)
            {
                return _entries.Count(e => (since == null || e.Timestamp >= since.Value) &&
                                           (e.Level == LogLevel.Error ||
                                            e.Message.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                                            e.Message.Contains("exception", StringComparison.OrdinalIgnoreCase)));
            }
        }

        public int GetWarningCount(DateTime? since = null)
        {
            lock (_lock)
            {
                return _entries.Count(e => (since == null || e.Timestamp >= since.Value) &&
                                           (e.Level == LogLevel.Warning ||
                                            e.Message.Contains("[!]") ||
                                            e.Message.Contains("[WARN]", StringComparison.OrdinalIgnoreCase)));
            }
        }

        public void Dispose()
        {
            _batchTimer.Dispose();
            FlushImmediate();
            lock (_lock)
            {
                _diskLogWriter?.Dispose();
                _diskLogWriter = null;
            }
        }
    }
}
