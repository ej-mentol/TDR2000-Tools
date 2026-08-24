using System;
using System.IO;
using System.Text.Json;

namespace TDR.Tools.Services
{
    public class AppSettings
    {
        public bool ConfirmOnDelete { get; set; } = true;
        public bool ShowDirIndexFiles { get; set; } = false; // .dir index files are half a format pair — hidden by default for newcomers, toggleable for reverse-engineers who need to see them

        public string LastSourceDirectory { get; set; } = string.Empty;
        public string LastDestinationDirectory { get; set; } = string.Empty;

        public string PakDragAction { get; set; } = "Ask"; // "Ask", "Extract", "Convert"
        public bool RememberPakDragAction { get; set; } = false;

        // Track discovery strategy:
        //   Auto       — try CARMA.pak/races.txt first, fall back to heuristic scan if not found
        //   RacesOnly  — only use races.txt; if missing, stop and report — no heuristic fallback
        //   Heuristic  — full Weak+Strong .txt scan (original behaviour, pre-races.txt era)
        public string TrackDiscoveryMode { get; set; } = "Auto"; // "Auto" | "RacesOnly" | "Heuristic"

        public bool ExportObj { get; set; } = true;
        public bool ExportGltf { get; set; } = true;
        public bool ExportArmatures { get; set; } = false;
        public bool ExportPngTextures { get; set; } = true;
        public bool IncludeMovableProps { get; set; } = true;
        public bool ExportSceneJson { get; set; } = true;
        public bool UseZeroOriginForJsonAssets { get; set; } = true;
        public bool UseGrouping { get; set; } = true;
        public bool UseLocalCoords { get; set; } = false;
        public bool VerboseLog { get; set; } = false;
        // DebugMode: timing and diagnostic info in the session log.
        // Separate from VerboseLog (export pipeline only) — this covers VFS index, tree build, discovery.
        public bool DebugMode { get; set; } = false;

        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TDR2000-Tools",
            "settings.json"
        );

        // Static cache: Load() is called on every RefreshSourceTree / RefreshDestinationTree
        // (triggered by search keystrokes, FileSystemWatcher events, etc.). Reading and
        // deserializing the JSON file on each call is wasteful. Cache is invalidated in Save()
        // which is the only place settings ever change at runtime.
        private static readonly object _lock = new();
        private static AppSettings? _cached;

        public static AppSettings Load()
        {
            lock (_lock)
            {
                if (_cached != null) return _cached;
                try
                {
                    if (File.Exists(SettingsFilePath))
                    {
                        string json = File.ReadAllText(SettingsFilePath);
                        _cached = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                        return _cached;
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warn($"[Settings] Failed to load settings.json: {ex.Message}");
                }
                _cached = new AppSettings();
                return _cached;
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(SettingsFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(SettingsFilePath, json);
                    // Update cache directly to the current state
                    _cached = this;
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warn($"[Settings] Failed to save settings.json: {ex.Message}");
                }
            }
        }
    }
}
