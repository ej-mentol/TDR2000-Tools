using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TDR.PakLib;
using TDR.PakLib.Formats;
using TDR.Tools.Export;
using TDR.Tools.Services;

namespace TDR.Tools.ViewModels
{
    public enum FileViewMode
    {
        Tree,
        DetailsList,
        GridTiles
    }

    public class MainViewModel : INotifyPropertyChanged
    {
        private PakManager _vfs = new();
        private string _sourceRootPath = string.Empty;
        private string _destinationRootPath = string.Empty;

        private FileViewMode _sourceViewMode = FileViewMode.Tree;
        private string _lastSortColumn = "Name";
        private bool _sortAscending = true;

        public FileViewMode SourceViewMode
        {
            get => _sourceViewMode;
            set
            {
                if (SetField(ref _sourceViewMode, value))
                {
                    OnPropertyChanged(nameof(IsSourceTreeVisible));
                    OnPropertyChanged(nameof(IsSourceFlatListVisible));
                    OnPropertyChanged(nameof(IsSourceGridVisible));
                    OnPropertyChanged(nameof(SourceTreeModeBrush));
                    OnPropertyChanged(nameof(SourceListModeBrush));
                    OnPropertyChanged(nameof(SourceGridModeBrush));
                    if (value != FileViewMode.Tree)
                    {
                        RefreshSourceFlatList();
                    }
                }
            }
        }

        public bool IsSourceTreeVisible => SourceViewMode == FileViewMode.Tree;
        public bool IsSourceFlatListVisible => SourceViewMode == FileViewMode.DetailsList;
        public bool IsSourceGridVisible => SourceViewMode == FileViewMode.GridTiles;

        public string SourceTreeModeBrush => IsSourceTreeVisible ? "#1F2D3A" : "Transparent";
        public string SourceListModeBrush => IsSourceFlatListVisible ? "#1F2D3A" : "Transparent";
        public string SourceGridModeBrush => IsSourceGridVisible ? "#1F2D3A" : "Transparent";

        public void RefreshSourceFlatList()
        {
            VfsTreeBuilder.BuildFlatList(_vfs, FlatSourceNodes, SearchSourceQuery, ValidateTrackContent);
            SortSourceFlatList(_lastSortColumn, toggleDirection: false);
        }

        public void SortSourceFlatList(string column, bool toggleDirection = true)
        {
            if (toggleDirection && _lastSortColumn == column)
            {
                _sortAscending = !_sortAscending;
            }
            else if (toggleDirection)
            {
                _lastSortColumn = column;
                _sortAscending = true;
            }

            var items = FlatSourceNodes.ToList();
            IEnumerable<FileNodeViewModel> sorted = column switch
            {
                "Size" => _sortAscending ? items.OrderBy(x => x.Size) : items.OrderByDescending(x => x.Size),
                "Type" => _sortAscending ? items.OrderBy(x => x.BadgeText) : items.OrderByDescending(x => x.BadgeText),
                "Source" => _sortAscending ? items.OrderBy(x => x.SourceArchiveName) : items.OrderByDescending(x => x.SourceArchiveName),
                _ => _sortAscending ? items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase) : items.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
            };

            FlatSourceNodes.Clear();
            foreach (var item in sorted)
            {
                FlatSourceNodes.Add(item);
            }
        }

        private FileSystemWatcher? _destinationWatcher;
        private System.Threading.Timer? _destinationDebounceTimer;
        private bool _suppressWatcherEvents = false;

        public string SourceRootPath => _sourceRootPath;
        public string DestinationRootPath => _destinationRootPath;

        private string _sourcePathText = "Source · Select ASSETS folder or PAK archive";
        private string _destinationPathText = "Destination · Select output folder on disk";

        private string _searchSourceQuery = string.Empty;
        private string _searchDestinationQuery = string.Empty;

        private FileNodeViewModel? _selectedSourceNode;
        private FileNodeViewModel? _selectedDestinationNode;

        private readonly Stack<string> _sourceHistoryBack = new();
        private readonly Stack<string> _sourceHistoryForward = new();

        private readonly Stack<string> _destinationHistoryBack = new();
        private readonly Stack<string> _destinationHistoryForward = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _isSourceLoading;
        private bool _isDestinationLoading;
        private bool _isBusy;
        private string _busyMessage = "Processing...";

        public bool IsSourceLoading
        {
            get => _isSourceLoading;
            set => SetField(ref _isSourceLoading, value);
        }

        public bool IsDestinationLoading
        {
            get => _isDestinationLoading;
            set => SetField(ref _isDestinationLoading, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetField(ref _isBusy, value);
        }

        public string BusyMessage
        {
            get => _busyMessage;
            set => SetField(ref _busyMessage, value);
        }

        private double _busyProgressValue;
        private bool _isBusyProgressIndeterminate = true;
        private double _busySubProgressValue;
        private bool _isBusySubProgressVisible;
        private string _busySubMessage = string.Empty;

        public double BusyProgressValue
        {
            get => _busyProgressValue;
            set => SetField(ref _busyProgressValue, value);
        }

        public bool IsBusyProgressIndeterminate
        {
            get => _isBusyProgressIndeterminate;
            set => SetField(ref _isBusyProgressIndeterminate, value);
        }

        public double BusySubProgressValue
        {
            get => _busySubProgressValue;
            set => SetField(ref _busySubProgressValue, value);
        }

        public bool IsBusySubProgressVisible
        {
            get => _isBusySubProgressVisible;
            set => SetField(ref _isBusySubProgressVisible, value);
        }

        public string BusySubMessage
        {
            get => _busySubMessage;
            set => SetField(ref _busySubMessage, value);
        }

        public void ReportProgress(double percent, string? message = null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                BusyProgressValue = Math.Clamp(percent, 0, 100);
                IsBusyProgressIndeterminate = false;
                if (!string.IsNullOrEmpty(message))
                {
                    BusyMessage = message;
                }
            });
        }

        public void ReportSubProgress(double percent, string? message = null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                BusySubProgressValue = Math.Clamp(percent, 0, 100);
                IsBusySubProgressVisible = true;
                if (!string.IsNullOrEmpty(message))
                {
                    BusySubMessage = message;
                }
            });
        }

        private void SetBusy(bool busy, string message = "")
        {
            IsBusy = busy;
            if (busy)
            {
                BusyProgressValue = 0;
                BusySubProgressValue = 0;
                IsBusySubProgressVisible = false;
                BusySubMessage = string.Empty;
                IsBusyProgressIndeterminate = true;
                if (!string.IsNullOrEmpty(message))
                {
                    BusyMessage = message;
                }
            }
        }

        public string SourcePathText
        {
            get => _sourcePathText;
            set => SetField(ref _sourcePathText, value);
        }

        public string DestinationPathText
        {
            get => _destinationPathText;
            set => SetField(ref _destinationPathText, value);
        }

        public ObservableCollection<FileNodeViewModel> SourceNodes { get; } = new();
        public ObservableCollection<FileNodeViewModel> FlatSourceNodes { get; } = new();
        public ObservableCollection<FileNodeViewModel> DestinationNodes { get; } = new();
        public ObservableCollection<string> LogLines { get; } = new();

        public string SearchSourceQuery
        {
            get => _searchSourceQuery;
            set
            {
                if (SetField(ref _searchSourceQuery, value))
                {
                    RefreshSourceTree();
                    if (SourceViewMode != FileViewMode.Tree)
                    {
                        RefreshSourceFlatList();
                    }
                }
            }
        }

        public string SearchDestinationQuery
        {
            get => _searchDestinationQuery;
            set
            {
                if (SetField(ref _searchDestinationQuery, value))
                {
                    RefreshDestinationTree();
                }
            }
        }

        // Preview / Inspector drawer — extracted to PreviewViewModel.
        // Lazily initialized in the constructor with delegates for file reading and logging.
        public PreviewViewModel Preview { get; private set; } = null!;
        public ObservableCollection<FileNodeViewModel> SelectedSourceNodes { get; } = new();
        public ObservableCollection<FileNodeViewModel> SelectedDestinationNodes { get; } = new();

        public FileNodeViewModel? SelectedSourceNode
        {
            get => _selectedSourceNode;
            set
            {
                if (SetField(ref _selectedSourceNode, value))
                {
                    ClearEditingStateExcept(value);
                    if (value != null)
                    {
                        Preview.UpdatePreviewNode(value);
                    }
                }
            }
        }

        public FileNodeViewModel? SelectedDestinationNode
        {
            get => _selectedDestinationNode;
            set
            {
                if (SetField(ref _selectedDestinationNode, value))
                {
                    ClearEditingStateExcept(value);
                    if (value != null)
                    {
                        Preview.UpdatePreviewNode(value);
                    }
                }
            }
        }

        public Func<ConvertTrackModalViewModel, Task>? RequestShowConvertModal { get; set; }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        // ClosePreview, UpdatePreviewNode, IsPrintableText → now in PreviewViewModel

        public MainViewModel()
        {
            Preview = new PreviewViewModel(ReadAllBytesForNode, LogSession);
        }

        public void InitializeStartup()
        {
            LogSession("Initialized TDR Tools UI session");

            var settings = AppSettings.Load();

            // Auto-initialize destination to last saved path or default EXPORT staging folder
            string defaultExportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TDR2000Tools", "Export");
            string initialDest = (!string.IsNullOrEmpty(settings.LastDestinationDirectory) && Directory.Exists(settings.LastDestinationDirectory))
                ? settings.LastDestinationDirectory
                : defaultExportPath;
            Directory.CreateDirectory(initialDest);
            SetDestinationDirectory(initialDest);

            // Auto-initialize Source: check if last saved path is a valid TDR2000 game assets directory
            string currentDir = Directory.GetCurrentDirectory();
            string initialSource = currentDir;

            if (!string.IsNullOrEmpty(settings.LastSourceDirectory))
            {
                if (TrackDiscoveryService.IsGameAssetsDirectory(settings.LastSourceDirectory))
                {
                    initialSource = settings.LastSourceDirectory;
                }
                else
                {
                    // Purge stale/invalid project source paths from settings.json
                    settings.LastSourceDirectory = string.Empty;
                    try { settings.Save(); } catch { }
                }
            }

            if (initialSource == currentDir)
            {
                string resolvedCurrent = TrackDiscoveryService.ResolveAssetsRootPath(currentDir);
                if (TrackDiscoveryService.IsGameAssetsDirectory(resolvedCurrent))
                {
                    initialSource = resolvedCurrent;
                }
            }

            _ = IndexDirectory(initialSource);
            LogSession("--------------------------------------------------");
        }

        public void LogSession(string message)
        {
            string timeStamp = DateTime.Now.ToString("HH:mm:ss");
            LogLines.Add($"[{timeStamp}] {message}");
        }

        public async Task RunWithWatchdogAsync(string opName, Func<Task> action, Action setProgressState, Action clearProgressState, int timeoutMs = 10000)
        {
            setProgressState();
            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);

            try
            {
                var task = action();
                var completedTask = await Task.WhenAny(task, Task.Delay(timeoutMs, cts.Token));

                if (completedTask != task)
                {
                    LogSession($"[WATCHDOG GUARD] '{opName}' exceeded {timeoutMs / 1000}s limit. UI unlocked automatically.");
                }
                else
                {
                    await task;
                }
            }
            catch (Exception ex)
            {
                LogSession($"[ERROR] '{opName}' failed: {ex.Message}");
            }
            finally
            {
                cts.Cancel();
                clearProgressState();
            }
        }

        public async Task IndexDirectory(string path, bool autoResolveRoot = true)
        {
            if (string.IsNullOrWhiteSpace(path) || (!Directory.Exists(path) && !File.Exists(path))) return;

            // autoResolveRoot=false is used when the caller (e.g. SourceNavigateUp) has already
            // decided the exact path to index and must not have it snapped back to the
            // nearest assets/tracks root by TrackDiscovery — otherwise navigating "up" out of
            // a resolved root just re-resolves back to the same root and the user gets stuck.
            string rootPath = autoResolveRoot ? TrackDiscoveryService.ResolveAssetsRootPath(path) : path;
            if (!string.IsNullOrEmpty(_sourceRootPath) && !_sourceRootPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                _sourceHistoryBack.Push(_sourceRootPath);
                _sourceHistoryForward.Clear();
            }

            _sourceRootPath = rootPath;
            SourcePathText = $"VFS:// {rootPath}";

            try
            {
                if (TrackDiscoveryService.IsGameAssetsDirectory(rootPath))
                {
                    var s = AppSettings.Load();
                    s.LastSourceDirectory = rootPath;
                    s.Save();
                }
            }
            catch { }

            var indexSw = System.Diagnostics.Stopwatch.StartNew();
            await RunWithWatchdogAsync("VFS Directory Indexing", () => Task.Run(() =>
            {
                var freshVfs = new PakManager();
                freshVfs.IndexDirectory(rootPath);

                // Ensure parent shared directories / archives (MOVABLEOBJECTS, POWERUPS, SHARED, TEXTURES) are indexed
                try
                {
                    string? parentDir = Path.GetDirectoryName(rootPath);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                    {
                        string[] sharedFolders = new[] { "MOVABLEOBJECTS", "POWERUPS", "SHARED", "TEXTURES" };
                        foreach (string folder in sharedFolders)
                        {
                            string folderPak = Path.Combine(parentDir, folder, $"{folder}.pak");
                            string folderDir = Path.Combine(parentDir, folder);
                            if (File.Exists(folderPak)) freshVfs.IndexDirectory(folderPak);
                            else if (Directory.Exists(folderDir)) freshVfs.IndexDirectory(folderDir);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogSession($"[!] Warning during parent shared assets auto-indexing: {ex.Message}");
                }

                _vfs = freshVfs;
            }),
            () => IsSourceLoading = true,
            () => IsSourceLoading = false,
            15000);
            indexSw.Stop();

            LogSession($"Indexed: {rootPath} | {_vfs.GetFiles().Count} VFS files");
            if (Services.AppSettings.Load().DebugMode)
                LogSession($"[DEBUG] IndexDirectory: {indexSw.ElapsedMilliseconds} ms | {_vfs.GetFiles().Count} files");
            RefreshSourceTree();
        }

        public void SetDestinationDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                bool wasMissing = !Directory.Exists(path);
                Directory.CreateDirectory(path);
                string fullPath = Path.GetFullPath(path);

                if (!string.IsNullOrEmpty(_destinationRootPath) && !_destinationRootPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    _destinationHistoryBack.Push(_destinationRootPath);
                    _destinationHistoryForward.Clear();
                }

                _destinationRootPath = fullPath;
                DestinationPathText = $"Disk:// {fullPath}";

                try
                {
                    var s = AppSettings.Load();
                    s.LastDestinationDirectory = fullPath;
                    s.Save();
                }
                catch { }

                if (wasMissing)
                {
                    LogSession($"[INFO] Created missing destination folder: {fullPath}");
                }
                else
                {
                    LogSession($"Destination set: {fullPath}");
                }

                SetupDestinationWatcher(fullPath);
                RefreshDestinationTree();
            }
            catch (Exception ex)
            {
                LogSession($"[ERROR] Failed to set destination directory '{path}': {ex.Message}");
            }
        }

        private void SetupDestinationWatcher(string path)
        {
            try
            {
                _destinationWatcher?.Dispose();
                _destinationWatcher = null;

                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

                _destinationWatcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };

                _destinationWatcher.Created += (s, e) => { if (!_suppressWatcherEvents) OnDestinationDiskChanged(); };
                _destinationWatcher.Deleted += (s, e) => { if (!_suppressWatcherEvents) OnDestinationDiskChanged(); };
                _destinationWatcher.Renamed += (s, e) => { if (!_suppressWatcherEvents) OnDestinationDiskChanged(); };
                _destinationWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                LogSession($"[Watchdog Warning] Could not initialize Destination disk watcher: {ex.Message}");
            }
        }

        private void OnDestinationDiskChanged()
        {
            if (_suppressWatcherEvents) return;

            _destinationDebounceTimer?.Dispose();
            _destinationDebounceTimer = new System.Threading.Timer(_ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_suppressWatcherEvents) return;
                    RefreshDestinationTree();
                    LogSession("[Watchdog Guard] External disk change detected in Destination -> Tree auto-refreshed.");
                });
            }, null, 300, System.Threading.Timeout.Infinite);
        }

        // --- SOURCE NAVIGATION ---
        public void SourceNavigateUp()
        {
            if (string.IsNullOrEmpty(_sourceRootPath)) return;
            var parent = Directory.GetParent(_sourceRootPath);
            if (parent != null)
            {
                LogSession($"Navigating Source UP to '{parent.FullName}'");
                _ = IndexDirectory(parent.FullName, false);
            }
        }

        public bool CanSourceNavigateBack => _sourceHistoryBack.Count > 0;
        public bool CanSourceNavigateForward => _sourceHistoryForward.Count > 0;
        public bool CanDestinationNavigateBack => _destinationHistoryBack.Count > 0;
        public bool CanDestinationNavigateForward => _destinationHistoryForward.Count > 0;

        private void NotifyNavPropertiesChanged()
        {
            OnPropertyChanged(nameof(CanSourceNavigateBack));
            OnPropertyChanged(nameof(CanSourceNavigateForward));
            OnPropertyChanged(nameof(CanDestinationNavigateBack));
            OnPropertyChanged(nameof(CanDestinationNavigateForward));
        }

        public void SourceNavigateBack()
        {
            if (_sourceHistoryBack.Count > 0)
            {
                string prev = _sourceHistoryBack.Pop();
                _sourceHistoryForward.Push(_sourceRootPath);
                _sourceRootPath = prev;
                SourcePathText = $"VFS:// {prev}";
                _vfs.IndexDirectory(prev);
                LogSession($"Source Navigate Back to '{prev}'");
                RefreshSourceTree();
                NotifyNavPropertiesChanged();
            }
        }

        public void SourceNavigateForward()
        {
            if (_sourceHistoryForward.Count > 0)
            {
                string next = _sourceHistoryForward.Pop();
                _sourceHistoryBack.Push(_sourceRootPath);
                _sourceRootPath = next;
                SourcePathText = $"VFS:// {next}";
                _vfs.IndexDirectory(next);
                LogSession($"Source Navigate Forward to '{next}'");
                RefreshSourceTree();
                NotifyNavPropertiesChanged();
            }
        }

        public byte[]? ReadAllBytesForNode(FileNodeViewModel node)
        {
            if (node == null) return null;

            if (!string.IsNullOrEmpty(node.VirtualPath))
            {
                byte[]? data = _vfs.LoadFile(node.VirtualPath);
                if (data != null) return data;
            }

            if (!string.IsNullOrEmpty(node.AbsolutePath) && File.Exists(node.AbsolutePath))
            {
                return File.ReadAllBytes(node.AbsolutePath);
            }

            return null;
        }

        public static void OpenSystemProperties(string filePath, IntPtr hwnd = default)
        {
            if (string.IsNullOrEmpty(filePath) || (!File.Exists(filePath) && !Directory.Exists(filePath))) return;

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    TDR.Tools.Utilities.WindowsShell.ShowProperties(filePath, hwnd);
                }
                else
                {
                    string? parent = Directory.Exists(filePath) ? filePath : Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(parent)) OpenWithDefaultApp(parent);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OpenSystemProperties Error] {ex.Message}");
            }
        }

        public static void OpenWithDefaultApp(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OpenWithDefaultApp Error] {ex.Message}");
            }
        }

        public static void OpenContainingFolderAndSelectFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            try
            {
                string targetPath = filePath;

                // If path does not exist on disk (e.g. inside a PAK archive), fallback to nearest parent directory or PAK file
                if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
                {
                    string? dir = Path.GetDirectoryName(targetPath);
                    while (!string.IsNullOrEmpty(dir) && !File.Exists(dir) && !Directory.Exists(dir))
                    {
                        dir = Path.GetDirectoryName(dir);
                    }
                    if (!string.IsNullOrEmpty(dir)) targetPath = dir;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string winPath = Path.GetFullPath(targetPath).Replace('/', '\\');
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{winPath}\"",
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (File.Exists(targetPath) || Directory.Exists(targetPath))
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("nautilus", $"--select \"{targetPath}\"") { UseShellExecute = true });
                            return;
                        }
                        catch { }
                    }

                    string? parent = File.Exists(targetPath) ? Path.GetDirectoryName(targetPath) : targetPath;
                    if (!string.IsNullOrEmpty(parent)) OpenWithDefaultApp(parent);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    System.Diagnostics.Process.Start("open", $"-R \"{targetPath}\"");
                }
                else
                {
                    string? parent = File.Exists(targetPath) ? Path.GetDirectoryName(targetPath) : targetPath;
                    if (!string.IsNullOrEmpty(parent)) OpenWithDefaultApp(parent);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OpenContainingFolder Error] {ex.Message}");
                string? parent = File.Exists(filePath) ? Path.GetDirectoryName(filePath) : filePath;
                if (!string.IsNullOrEmpty(parent)) OpenWithDefaultApp(parent);
            }
        }

        public void NavigateIntoSourceNode(FileNodeViewModel node)
        {
            if (node == null) return;

            if (node.Name == "..")
            {
                SourceNavigateUp();
                return;
            }

            if (node.IsDirectory || node.IsArchive)
            {
                // node.Children is already populated for the whole VFS by RefreshSourceTree()
                // (which runs off a single full recursive index of _sourceRootPath), so expanding
                // a node here is a pure UI toggle. Re-indexing on every expand used to re-resolve
                // the assets root and rebuild the entire PakManager from scratch on every click.
                node.IsExpanded = !node.IsExpanded;
            }
            else if (!string.IsNullOrEmpty(node.AbsolutePath) && File.Exists(node.AbsolutePath))
            {
                OpenWithDefaultApp(node.AbsolutePath);
                LogSession($"ShellExecute opened file: {node.Name}");
            }
        }

        public void NavigateIntoDestinationNode(FileNodeViewModel node)
        {
            if (node == null) return;

            if (node.Name == "..")
            {
                DestinationNavigateUp();
                return;
            }

            if (node.IsDirectory)
            {
                node.IsExpanded = !node.IsExpanded;
                if (!string.IsNullOrEmpty(node.AbsolutePath) && Directory.Exists(node.AbsolutePath))
                {
                    SetDestinationDirectory(node.AbsolutePath);
                }
            }
            else if (!string.IsNullOrEmpty(node.AbsolutePath) && File.Exists(node.AbsolutePath))
            {
                OpenWithDefaultApp(node.AbsolutePath);
                LogSession($"ShellExecute opened file: {node.Name}");
            }
        }

        // --- DESTINATION NAVIGATION ---
        public void DestinationNavigateUp()
        {
            if (string.IsNullOrEmpty(_destinationRootPath)) return;
            var parent = Directory.GetParent(_destinationRootPath);
            if (parent != null)
            {
                LogSession($"Navigating Destination UP to '{parent.FullName}'");
                SetDestinationDirectory(parent.FullName);
            }
        }

        public void DestinationNavigateBack()
        {
            if (_destinationHistoryBack.Count > 0)
            {
                string prev = _destinationHistoryBack.Pop();
                _destinationHistoryForward.Push(_destinationRootPath);
                _destinationRootPath = prev;
                DestinationPathText = $"Disk:// {prev}";
                LogSession($"Destination Navigate Back to '{prev}'");
                RefreshDestinationTree();
                NotifyNavPropertiesChanged();
            }
        }

        public void DestinationNavigateForward()
        {
            if (_destinationHistoryForward.Count > 0)
            {
                string next = _destinationHistoryForward.Pop();
                _destinationHistoryBack.Push(_destinationRootPath);
                _destinationRootPath = next;
                DestinationPathText = $"Disk:// {next}";
                LogSession($"Destination Navigate Forward to '{next}'");
                RefreshDestinationTree();
                NotifyNavPropertiesChanged();
            }
        }

        public void RefreshSourceTree()
        {
            if (string.IsNullOrEmpty(_sourceRootPath)) return;
            var settings = Services.AppSettings.Load();

            var expandedPaths = CollectExpandedPaths(SourceNodes);
            string? selectedPath = SelectedSourceNode?.VirtualPath;

            if (settings.DebugMode)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _debugWeakCandidates = 0;
                _debugStrongConfirmed = 0;
                VfsTreeBuilder.BuildSourceTree(_vfs, _sourceRootPath, SourceNodes, ValidateTrackContent, SearchSourceQuery, settings.ShowDirIndexFiles);
                sw.Stop();
                LogSession($"[DEBUG] BuildSourceTree: {sw.ElapsedMilliseconds} ms | weak={_debugWeakCandidates} strong={_debugStrongConfirmed}");
            }
            else
            {
                VfsTreeBuilder.BuildSourceTree(_vfs, _sourceRootPath, SourceNodes, ValidateTrackContent, SearchSourceQuery, settings.ShowDirIndexFiles);
            }

            RestoreExpandedPaths(SourceNodes, expandedPaths);
            if (!string.IsNullOrEmpty(selectedPath))
            {
                SelectedSourceNode = FindNodeByVirtualPath(SourceNodes, selectedPath);
            }
        }

        // Debug counters — only written when DebugMode is on, read in RefreshSourceTree
        private int _debugWeakCandidates = 0;
        private int _debugStrongConfirmed = 0;

        private bool ValidateTrackContent(string virtualPath)
        {
            // RacesOnly mode: badges come from races.txt only — skip heuristic content scan.
            string mode = Services.AppSettings.Load().TrackDiscoveryMode;
            if (mode.Equals("RacesOnly", StringComparison.OrdinalIgnoreCase)) return false;

            if (!TrackDiscovery.IsWeakTrackCandidate(virtualPath)) return false;
            _debugWeakCandidates++;

            try
            {
                byte[]? data = _vfs.LoadFile(virtualPath);
                bool strong = TrackDiscovery.IsStrongTrackContent(data);
                if (strong) _debugStrongConfirmed++;
                return strong;
            }
            catch
            {
                return false;
            }
        }

        public void RefreshDestinationTree()
        {
            var expandedPaths = CollectExpandedPaths(DestinationNodes);
            string? selectedPath = SelectedDestinationNode?.VirtualPath;

            VfsTreeBuilder.BuildDestinationTree(_destinationRootPath, DestinationNodes, SearchDestinationQuery, LogSession, AppSettings.Load().ShowDirIndexFiles);

            RestoreExpandedPaths(DestinationNodes, expandedPaths);
            if (!string.IsNullOrEmpty(selectedPath))
            {
                SelectedDestinationNode = FindNodeByVirtualPath(DestinationNodes, selectedPath);
            }
        }

        private static HashSet<string> CollectExpandedPaths(IEnumerable<FileNodeViewModel> nodes)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectExpandedRecursive(nodes, set);
            return set;
        }

        private static void CollectExpandedRecursive(IEnumerable<FileNodeViewModel> nodes, HashSet<string> set)
        {
            foreach (var node in nodes)
            {
                if (node.IsExpanded && !string.IsNullOrEmpty(node.VirtualPath))
                {
                    set.Add(node.VirtualPath);
                }
                if (node.Children.Count > 0)
                {
                    CollectExpandedRecursive(node.Children, set);
                }
            }
        }

        private static void RestoreExpandedPaths(IEnumerable<FileNodeViewModel> nodes, HashSet<string> set)
        {
            foreach (var node in nodes)
            {
                if (!string.IsNullOrEmpty(node.VirtualPath) && set.Contains(node.VirtualPath))
                {
                    node.IsExpanded = true;
                }
                if (node.Children.Count > 0)
                {
                    RestoreExpandedPaths(node.Children, set);
                }
            }
        }

        private static FileNodeViewModel? FindNodeByVirtualPath(IEnumerable<FileNodeViewModel> nodes, string targetPath)
        {
            foreach (var node in nodes)
            {
                if (node.VirtualPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase)) return node;
                if (node.Children.Count > 0)
                {
                    var found = FindNodeByVirtualPath(node.Children, targetPath);
                    if (found != null) return found;
                }
            }
            return null;
        }

        public void PackFolderToPak(string folderPath, string outputPakPath, bool compress = true)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;
            LogSession($"[+] Packing directory '{folderPath}' into PAK archive '{outputPakPath}'...");

            _suppressWatcherEvents = true;
            try
            {
                bool success = PakPacker.PackDirectory(folderPath, outputPakPath, compress, LogSession);
                if (success)
                {
                    RefreshSourceTree();
                }
            }
            finally
            {
                _suppressWatcherEvents = false;
                RefreshDestinationTree();
            }
        }

        public void ExtractNodeToDestination(FileNodeViewModel node, bool createSubfolderForPak = true, bool flatFiles = false, bool unpackOnly = false)
        {
            if (node == null) return;

            if (string.IsNullOrEmpty(_destinationRootPath))
            {
                string defaultExport = Path.Combine(Directory.GetCurrentDirectory(), "EXPORT");
                SetDestinationDirectory(defaultExport);
            }

            string targetDir = _destinationRootPath;
            if ((node.IsArchive || node.IsDirectory) && createSubfolderForPak)
            {
                string subfolderName = Path.GetFileNameWithoutExtension(node.Name);
                targetDir = Path.Combine(_destinationRootPath, subfolderName);
            }

            _suppressWatcherEvents = true;
            try
            {
                ExtractNodeIntoFolder(node, targetDir, flatFiles, unpackOnly);
            }
            finally
            {
                RefreshDestinationTree();
                _suppressWatcherEvents = false;
            }
        }

        private void ExtractNodeIntoFolder(FileNodeViewModel node, string targetDir, bool flatFiles, bool unpackOnly = false)
        {
            if (node.IsDirectory || node.IsArchive)
            {
                string prefix = node.VirtualPath;

                var filesToExtract = _vfs.GetFiles()
                    .Where(f => (!string.IsNullOrEmpty(prefix) && f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrEmpty(f.ArchivePath) && !string.IsNullOrEmpty(node.AbsolutePath) &&
                                 (f.ArchivePath.Equals(node.AbsolutePath, StringComparison.OrdinalIgnoreCase) ||
                                  f.ArchivePath.StartsWith(node.AbsolutePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                  f.ArchivePath.StartsWith(node.AbsolutePath + "/", StringComparison.OrdinalIgnoreCase))))
                    .Where(f => !unpackOnly || (!f.IsLooseFile && !string.IsNullOrEmpty(f.ArchivePath)))
                    .ToList();

                if (filesToExtract.Count == 0)
                {
                    LogSession($"[!] Warning: 0 files found in '{node.Name}'. Extraction cancelled (no empty directory created).");
                    return;
                }

                LogSession($"Extracting {filesToExtract.Count} files from '{node.Name}' to {targetDir}...");
                Directory.CreateDirectory(targetDir);

                int count = 0;
                foreach (var f in filesToExtract)
                {
                    byte[]? data = _vfs.LoadFile(f);
                    if (data != null)
                    {
                        string relPath = flatFiles ? Path.GetFileName(f.Name) : f.Name;
                        if (!flatFiles && !string.IsNullOrEmpty(prefix) && f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            relPath = f.Name.Substring(prefix.Length).TrimStart('/', '\\');
                        }

                        string outPath = Path.Combine(targetDir, relPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                        File.WriteAllBytes(outPath, data);
                        count++;
                    }
                }
                LogSession($"Extracted {count} files from '{node.Name}' to {targetDir}");
            }
            else
            {
                byte[]? data = _vfs.LoadFile(node.VirtualPath);
                if (data == null && !string.IsNullOrEmpty(node.Name))
                {
                    data = _vfs.LoadFile(node.Name);
                }

                if (data == null && !string.IsNullOrEmpty(node.AbsolutePath) && File.Exists(node.AbsolutePath) && !node.AbsolutePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                {
                    data = File.ReadAllBytes(node.AbsolutePath);
                }

                if (data != null && data.Length > 0)
                {
                    Directory.CreateDirectory(targetDir);
                    string outPath = Path.Combine(targetDir, node.Name);
                    File.WriteAllBytes(outPath, data);
                    LogSession($"Extracted '{node.Name}' ({data.Length} bytes) to {targetDir}");
                }
                else
                {
                    LogSession($"[!] Warning: File '{node.Name}' is empty or unreadable. Skipped.");
                }
            }
        }

        public void ExtractSelected()
        {
            var targets = SelectedSourceNodes.Count > 0
                ? SelectedSourceNodes.ToList()
                : (SelectedSourceNode != null ? new List<FileNodeViewModel> { SelectedSourceNode } : new List<FileNodeViewModel>());

            if (targets.Count == 0)
            {
                LogSession("Extract cancelled: No item selected");
                return;
            }

            foreach (var node in targets)
            {
                ExtractNodeToDestination(node, createSubfolderForPak: true);
            }
        }

        public async Task DeleteSelectedSourceNodeAsync(List<FileNodeViewModel>? explicitTargets = null, bool permanent = false)
        {
            try
            {
                var targets = explicitTargets ?? (SelectedSourceNodes.Count > 0
                    ? SelectedSourceNodes.ToList()
                    : (SelectedSourceNode != null ? new List<FileNodeViewModel> { SelectedSourceNode } : new List<FileNodeViewModel>()));

                if (targets.Count == 0) return;

                foreach (var node in targets)
                {
                    if (node.Name == "..") continue;

                    string? path = node.AbsolutePath;
                    if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
                    {
                        if (!string.IsNullOrEmpty(node.VirtualPath) && !string.IsNullOrEmpty(_sourceRootPath))
                        {
                            string resolvedDiskPath = Path.GetFullPath(Path.Combine(_sourceRootPath, node.VirtualPath));
                            if (File.Exists(resolvedDiskPath) || Directory.Exists(resolvedDiskPath))
                            {
                                path = resolvedDiskPath;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                    {
                        try
                        {
                            if (File.Exists(path))
                            {
                                Utilities.WindowsShell.SendToRecycleBin(path, permanent, confirm: false);
                                if (File.Exists(path)) File.Delete(path);

                                if (path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                                {
                                    string matchingDir = Path.ChangeExtension(path, ".dir");
                                    if (File.Exists(matchingDir))
                                    {
                                        Utilities.WindowsShell.SendToRecycleBin(matchingDir, permanent, confirm: false);
                                        if (File.Exists(matchingDir)) File.Delete(matchingDir);
                                    }
                                }
                                else if (path.EndsWith(".dir", StringComparison.OrdinalIgnoreCase))
                                {
                                    string matchingPak = Path.ChangeExtension(path, ".pak");
                                    if (File.Exists(matchingPak))
                                    {
                                        Utilities.WindowsShell.SendToRecycleBin(matchingPak, permanent, confirm: false);
                                        if (File.Exists(matchingPak)) File.Delete(matchingPak);
                                    }
                                }
                                LogSession($"Deleted file: '{node.Name}'");
                            }
                            else if (Directory.Exists(path))
                            {
                                Utilities.WindowsShell.SendToRecycleBin(path, permanent, confirm: false);
                                if (Directory.Exists(path))
                                {
                                    Directory.Delete(path, recursive: true);
                                }
                                LogSession($"Deleted directory: '{node.Name}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogSession($"[ERROR] Failed to delete '{node.Name}': {ex.Message}");
                        }
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(node.VirtualPath))
                        {
                            DeletePakInnerFile(node.VirtualPath);
                        }
                        else
                        {
                            LogSession($"[!] Delete skipped: '{node.Name}' does not exist on disk.");
                        }
                    }
                }

                string? parentPath = Path.GetDirectoryName(targets[0].VirtualPath)?.Replace('\\', '/') ?? "";
                SelectedSourceNodes.Clear();
                SelectedSourceNode = null;
                await IndexDirectory(_sourceRootPath);

                if (!string.IsNullOrEmpty(parentPath))
                {
                    SelectedSourceNode = FindNodeByVirtualPath(SourceNodes, parentPath);
                }
            }
            catch (Exception ex)
            {
                LogSession($"[ERROR] Deletion task failed: {ex.Message}");
            }
        }

        private void DeletePakInnerFile(string virtualPath)
        {
            if (string.IsNullOrEmpty(virtualPath) || string.IsNullOrEmpty(_sourceRootPath)) return;

            string normVirtual = virtualPath.Replace('\\', '/');
            int pakExtIndex = normVirtual.IndexOf(".pak/", StringComparison.OrdinalIgnoreCase);
            if (pakExtIndex < 0) return;

            string pakRelPath = normVirtual.Substring(0, pakExtIndex + 4);
            string innerRelPath = normVirtual.Substring(pakExtIndex + 5);

            string pakPath = Path.GetFullPath(Path.Combine(_sourceRootPath, pakRelPath));
            string dirPath = Path.ChangeExtension(pakPath, ".dir");

            if (!File.Exists(dirPath)) return;

            try
            {
                var entries = TDRArchive.ParseTrieIndex(dirPath);
                int initialCount = entries.Count;

                entries.RemoveAll(e => e.Name.Replace('\\', '/').Equals(innerRelPath, StringComparison.OrdinalIgnoreCase));

                if (entries.Count < initialCount)
                {
                    if (entries.Count == 0)
                    {
                        if (File.Exists(dirPath)) File.Delete(dirPath);
                        if (File.Exists(pakPath)) File.Delete(pakPath);
                        LogSession($"[+] Archive '{Path.GetFileName(pakPath)}' became empty and was cleanly removed from disk.");
                    }
                    else
                    {
                        byte[] updatedDirData = TDRArchive.SerializeTrieIndex(entries);
                        string tmpDir = dirPath + ".tmp";
                        File.WriteAllBytes(tmpDir, updatedDirData);
                        File.Move(tmpDir, dirPath, overwrite: true);
                        LogSession($"[+] Removed entry '{innerRelPath}' from archive index '{Path.GetFileName(dirPath)}'. Run 'Rebuild Archive' to defragment storage.");
                    }
                }
            }
            catch (Exception ex)
            {
                LogSession($"[ERROR] Failed to remove inner file from '{Path.GetFileName(dirPath)}': {ex.Message}");
            }
        }

        public void DeleteSelectedSourceNode(bool permanent = false)
        {
            _ = DeleteSelectedSourceNodeAsync(null, permanent);
        }

        public void DeleteSelectedDestinationNode(List<FileNodeViewModel>? explicitTargets = null, bool permanent = false)
        {
            var targets = explicitTargets ?? (SelectedDestinationNodes.Count > 0
                ? SelectedDestinationNodes.ToList()
                : (SelectedDestinationNode != null ? new List<FileNodeViewModel> { SelectedDestinationNode } : new List<FileNodeViewModel>()));

            if (targets.Count == 0) return;

            _suppressWatcherEvents = true;
            try
            {
                foreach (var node in targets)
                {
                    if (node.Name == ".." || node.Name == "(Empty Folder)" || node.VirtualPath == "(empty)") continue;

                    string? path = node.AbsolutePath;
                    if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
                    {
                        if (!string.IsNullOrEmpty(node.VirtualPath) && !string.IsNullOrEmpty(_destinationRootPath))
                        {
                            string resolvedDiskPath = Path.GetFullPath(Path.Combine(_destinationRootPath, node.VirtualPath));
                            if (File.Exists(resolvedDiskPath) || Directory.Exists(resolvedDiskPath))
                            {
                                path = resolvedDiskPath;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                    {
                        try
                        {
                            if (File.Exists(path))
                            {
                                Utilities.WindowsShell.SendToRecycleBin(path, permanent, confirm: false);
                                if (File.Exists(path)) File.Delete(path);

                                if (path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                                {
                                    string matchingDir = Path.ChangeExtension(path, ".dir");
                                    if (File.Exists(matchingDir))
                                    {
                                        Utilities.WindowsShell.SendToRecycleBin(matchingDir, permanent, confirm: false);
                                        if (File.Exists(matchingDir)) File.Delete(matchingDir);
                                    }
                                }
                                else if (path.EndsWith(".dir", StringComparison.OrdinalIgnoreCase))
                                {
                                    string matchingPak = Path.ChangeExtension(path, ".pak");
                                    if (File.Exists(matchingPak))
                                    {
                                        Utilities.WindowsShell.SendToRecycleBin(matchingPak, permanent, confirm: false);
                                        if (File.Exists(matchingPak)) File.Delete(matchingPak);
                                    }
                                }
                                LogSession($"Deleted file from Destination: '{node.Name}'");
                            }
                            else if (Directory.Exists(path))
                            {
                                Utilities.WindowsShell.SendToRecycleBin(path, permanent, confirm: false);
                                if (Directory.Exists(path))
                                {
                                    Directory.Delete(path, recursive: true);
                                }
                                LogSession($"Deleted directory from Destination: '{node.Name}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogSession($"[ERROR] Failed to delete '{node.Name}' from Destination: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                string? parentPath = Path.GetDirectoryName(targets[0].VirtualPath)?.Replace('\\', '/') ?? "";
                SelectedDestinationNodes.Clear();
                SelectedDestinationNode = null;
                RefreshDestinationTree();
                if (!string.IsNullOrEmpty(parentPath))
                {
                    SelectedDestinationNode = FindNodeByVirtualPath(DestinationNodes, parentPath);
                }
                _suppressWatcherEvents = false;
            }
        }

        public async Task ReindexAllAsync()
        {
            LogSession("[Watchdog] Re-index initiated...");
            if (!string.IsNullOrEmpty(_sourceRootPath))
            {
                await IndexDirectory(_sourceRootPath);
            }
            if (!string.IsNullOrEmpty(_destinationRootPath))
            {
                RefreshDestinationTree();
            }
            LogSession("[Watchdog] Re-index completed.");
        }

        public void CreateNewFolderInDestination(FileNodeViewModel? parentNode)
        {
            if (string.IsNullOrEmpty(_destinationRootPath))
            {
                string defaultExport = Path.Combine(Directory.GetCurrentDirectory(), "EXPORT");
                SetDestinationDirectory(defaultExport);
            }

            string targetDir = _destinationRootPath;
            if (parentNode != null && parentNode.IsDirectory && !string.IsNullOrEmpty(parentNode.AbsolutePath) && Directory.Exists(parentNode.AbsolutePath))
            {
                targetDir = parentNode.AbsolutePath;
            }

            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;

            string baseName = "New Folder";
            string folderPath = Path.Combine(targetDir, baseName);
            int counter = 1;
            while (Directory.Exists(folderPath))
            {
                folderPath = Path.Combine(targetDir, $"{baseName} ({counter})");
                counter++;
            }

            _suppressWatcherEvents = true;
            try
            {
                Directory.CreateDirectory(folderPath);
                LogSession($"Created new directory: '{Path.GetFileName(folderPath)}' in {targetDir}");
                RefreshDestinationTree();

                var createdNode = FindNodeByAbsolutePath(DestinationNodes, folderPath);
                if (createdNode != null)
                {
                    if (parentNode != null) parentNode.IsExpanded = true;
                    SelectedDestinationNode = createdNode;
                    createdNode.IsEditing = true;
                }
            }
            catch (Exception ex)
            {
                LogSession($"[ERROR] Failed to create directory: {ex.Message}");
            }
            finally
            {
                _suppressWatcherEvents = false;
            }
        }

        public bool ValidateFileName(string newName, string parentDir, string currentName, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(newName))
            {
                errorMessage = "Filename cannot be empty.";
                return false;
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            if (newName.IndexOfAny(invalidChars) >= 0)
            {
                errorMessage = $"Filename '{newName}' contains invalid system characters.";
                return false;
            }

            if (!newName.Equals(currentName, StringComparison.OrdinalIgnoreCase))
            {
                string targetPath = Path.Combine(parentDir, newName);
                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    errorMessage = $"A file or folder named '{newName}' already exists in this directory.";
                    return false;
                }

                if (newName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                {
                    string dirPath = Path.ChangeExtension(targetPath, ".dir");
                    if (File.Exists(dirPath))
                    {
                        errorMessage = $"An archive index file '{Path.GetFileName(dirPath)}' already exists in this directory.";
                        return false;
                    }
                }
            }

            return true;
        }

        public static void SortNodeCollection(ObservableCollection<FileNodeViewModel> collection)
        {
            var sorted = collection
                .OrderBy(n => n.Name == ".." ? 0 : 1)
                .ThenBy(n => GetSortPriority(n))
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                int oldIndex = collection.IndexOf(sorted[i]);
                if (oldIndex != i && oldIndex >= 0)
                {
                    collection.Move(oldIndex, i);
                }
            }
        }

        private static int GetSortPriority(FileNodeViewModel node)
        {
            if (node.IsDirectory) return 0; // 1. Folders FIRST
            return 1;                       // 2. All Files (including .pak archives) SECOND (sorted A-Z)
        }

        public async Task<FileNodeViewModel?> CreateNewPakArchiveAsync(FileNodeViewModel? parentNode)
        {
            string parentDir = _sourceRootPath;
            FileNodeViewModel? ownerFolderNode = null;

            if (parentNode != null)
            {
                if (parentNode.IsDirectory && !string.IsNullOrEmpty(parentNode.AbsolutePath) && Directory.Exists(parentNode.AbsolutePath))
                {
                    parentDir = parentNode.AbsolutePath;
                    ownerFolderNode = parentNode;
                }
                else if (parentNode.Parent != null && parentNode.Parent.IsDirectory)
                {
                    parentDir = parentNode.Parent.AbsolutePath;
                    ownerFolderNode = parentNode.Parent;
                }
            }

            if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
            {
                parentDir = !string.IsNullOrEmpty(_sourceRootPath) ? _sourceRootPath : Directory.GetCurrentDirectory();
            }

            string baseName = "Unnamed";
            string pakName = $"{baseName}.pak";
            string pakPath = Path.Combine(parentDir, pakName);

            int counter = 1;
            while (File.Exists(pakPath) || File.Exists(Path.ChangeExtension(pakPath, ".dir")))
            {
                pakName = $"{baseName} {counter}.pak";
                pakPath = Path.Combine(parentDir, pakName);
                counter++;
            }

            string virtPath = ownerFolderNode != null && !string.IsNullOrEmpty(ownerFolderNode.VirtualPath)
                ? Path.Combine(ownerFolderNode.VirtualPath, pakName).Replace('\\', '/')
                : pakName;

            // Virtual Draft creation (Lazy creation strategy: disk files written on drop)
            var draftNode = new FileNodeViewModel
            {
                Name = pakName,
                VirtualPath = virtPath,
                AbsolutePath = pakPath,
                IsArchive = true,
                IsVirtual = true,
                BadgeText = "DRAFT",
                Icon = "📦",
                Parent = ownerFolderNode
            };
            draftNode.UpdateIcon();

            var targetCollection = ownerFolderNode != null ? ownerFolderNode.Children : SourceNodes;
            targetCollection.Add(draftNode);
            if (ownerFolderNode != null) ownerFolderNode.IsExpanded = true;

            SortNodeCollection(targetCollection);

            SelectedSourceNode = draftNode;
            draftNode.IsEditing = true;
            LogSession($"[+] Created new PAK archive: '{pakName}' in '{parentDir}'.");

            return draftNode;
        }

        public async Task AddFilesToArchiveAsync(FileNodeViewModel targetPakNode, IEnumerable<string>? fileOrFolderPathsOnDisk, IEnumerable<FileNodeViewModel>? vfsNodes = null)
        {
            if (targetPakNode == null) return;

            string parentDir = !string.IsNullOrEmpty(_sourceRootPath) ? _sourceRootPath : Directory.GetCurrentDirectory();
            string pakPath = targetPakNode.AbsolutePath;
            if (string.IsNullOrEmpty(pakPath))
            {
                string name = targetPakNode.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ? targetPakNode.Name : $"{targetPakNode.Name}.pak";
                pakPath = Path.Combine(parentDir, name);
            }

            LogSession($"[+] Staging drop onto PAK archive '{Path.GetFileName(pakPath)}'...");
            var existingFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            // 1. Read existing files if physical PAK exists on disk
            if (File.Exists(pakPath))
            {
                var tempVfs = new PakManager();
                tempVfs.IndexDirectory(Path.GetDirectoryName(pakPath) ?? parentDir);
                foreach (var f in tempVfs.GetFiles())
                {
                    if (!string.IsNullOrEmpty(f.ArchivePath) && f.ArchivePath.Equals(pakPath, StringComparison.OrdinalIgnoreCase))
                    {
                        byte[]? data = tempVfs.LoadFile(f.Name);
                        if (data != null) existingFiles[f.Name.Replace('\\', '/').ToLowerInvariant()] = data;
                    }
                }
            }

            // 2. Process dropped files/folders from OS file manager
            if (fileOrFolderPathsOnDisk != null)
            {
                foreach (string path in fileOrFolderPathsOnDisk)
                {
                    if (File.Exists(path))
                    {
                        string rel = Path.GetFileName(path).ToLowerInvariant();
                        existingFiles[rel] = File.ReadAllBytes(path);
                        LogSession($"  [+] Added file from OS: '{rel}' ({existingFiles[rel].Length} bytes)");
                    }
                    else if (Directory.Exists(path))
                    {
                        string baseDirName = Path.GetFileName(path);
                        foreach (string f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                        {
                            string rel = Path.Combine(baseDirName, Path.GetRelativePath(path, f)).Replace('\\', '/').ToLowerInvariant();
                            existingFiles[rel] = File.ReadAllBytes(f);
                            LogSession($"  [+] Added folder file from OS: '{rel}' ({existingFiles[rel].Length} bytes)");
                        }
                    }
                }
            }

            // 3. Process dropped VFS nodes from internal tree
            if (vfsNodes != null)
            {
                foreach (var node in vfsNodes)
                {
                    if (!node.IsDirectory && !node.IsArchive)
                    {
                        byte[]? data = _vfs.LoadFile(node.VirtualPath);
                        if (data == null && !string.IsNullOrEmpty(node.AbsolutePath) && File.Exists(node.AbsolutePath))
                        {
                            data = File.ReadAllBytes(node.AbsolutePath);
                        }

                        if (data != null)
                        {
                            string rel = Path.GetFileName(node.VirtualPath).ToLowerInvariant();
                            existingFiles[rel] = data;
                            LogSession($"  [+] Added VFS file: '{rel}' ({data.Length} bytes)");
                        }
                    }
                }
            }

            // 4. Pack & update physical .PAK / .DIR archive on disk
            var fileList = existingFiles.Select(kv => new PakPacker.FileToPack { VirtualPath = kv.Key, Content = kv.Value });
            bool success = PakPacker.PackFiles(fileList, pakPath, compress: true, LogSession);

            if (success)
            {
                targetPakNode.IsVirtual = false;
                targetPakNode.AbsolutePath = pakPath;
                await IndexDirectory(_sourceRootPath);
            }
        }

        public FileNodeViewModel? CreateNewPakArchive(FileNodeViewModel? parentNode)
        {
            _ = CreateNewPakArchiveAsync(parentNode);
            return null;
        }

        public void ClearEditingStateExcept(FileNodeViewModel? keepNode = null)
        {
            void ClearCollection(IEnumerable<FileNodeViewModel> nodes)
            {
                foreach (var n in nodes)
                {
                    if (n != keepNode && n.IsEditing)
                    {
                        string newName = n.EditName;
                        n.IsEditing = false;
                        if (!string.IsNullOrWhiteSpace(newName) && newName != n.Name)
                        {
                            RenameNode(n, newName);
                        }
                        else
                        {
                            n.EditName = n.Name;
                        }
                    }
                    if (n.Children.Count > 0)
                    {
                        ClearCollection(n.Children);
                    }
                }
            }

            ClearCollection(SourceNodes);
            ClearCollection(DestinationNodes);
            ClearCollection(FlatSourceNodes);
        }

        public void RenameNode(FileNodeViewModel node, string newName)
        {
            if (node == null) return;
            node.IsEditing = false;
            if (string.IsNullOrWhiteSpace(newName) || newName == node.Name)
            {
                node.EditName = node.Name;
                return;
            }

            bool isDestNode = IsNodeInCollection(node, DestinationNodes);
            string defaultRoot = isDestNode ? _destinationRootPath : _sourceRootPath;

            string parentDir = !string.IsNullOrEmpty(node.AbsolutePath)
                ? (Path.GetDirectoryName(node.AbsolutePath) ?? defaultRoot)
                : defaultRoot;

            if (!ValidateFileName(newName, parentDir, node.Name, out string errorMsg))
            {
                LogSession($"[!] Rename failed for '{node.Name}': {errorMsg}");
                node.EditName = node.Name;
                return;
            }

            if (string.IsNullOrEmpty(node.AbsolutePath) || (!File.Exists(node.AbsolutePath) && !Directory.Exists(node.AbsolutePath)))
            {
                // Virtual node rename
                node.Name = newName;
                node.VirtualPath = node.Parent != null && !string.IsNullOrEmpty(node.Parent.VirtualPath)
                    ? Path.Combine(node.Parent.VirtualPath, newName).Replace('\\', '/')
                    : newName;
                node.AbsolutePath = Path.Combine(parentDir, newName);

                var coll = node.Parent != null ? node.Parent.Children : (isDestNode ? DestinationNodes : SourceNodes);
                SortNodeCollection(coll);
                if (isDestNode) SelectedDestinationNode = node;
                else SelectedSourceNode = node;
                LogSession($"Renamed virtual item -> '{newName}'");
                return;
            }

            string newPath = Path.Combine(parentDir, newName);

            _suppressWatcherEvents = true;
            try
            {
                if (node.IsDirectory)
                {
                    Directory.Move(node.AbsolutePath, newPath);
                    LogSession($"Renamed directory '{node.Name}' -> '{newName}'");
                }
                else
                {
                    File.Move(node.AbsolutePath, newPath);
                    if (node.IsArchive && node.AbsolutePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                    {
                        string oldDir = Path.ChangeExtension(node.AbsolutePath, ".dir");
                        string newDir = Path.ChangeExtension(newPath, ".dir");
                        if (File.Exists(oldDir)) File.Move(oldDir, newDir);
                    }
                    LogSession($"Renamed file '{node.Name}' -> '{newName}'");
                }

                node.Name = newName;
                node.AbsolutePath = newPath;
                node.VirtualPath = node.Parent != null && !string.IsNullOrEmpty(node.Parent.VirtualPath)
                    ? Path.Combine(node.Parent.VirtualPath, newName).Replace('\\', '/')
                    : newName;

                var parentColl = node.Parent != null ? node.Parent.Children : (isDestNode ? DestinationNodes : SourceNodes);
                SortNodeCollection(parentColl);
                if (isDestNode) SelectedDestinationNode = node;
                else SelectedSourceNode = node;
            }
            catch (Exception ex)
            {
                LogSession($"[ERROR] Failed to rename '{node.Name}': {ex.Message}");
                node.EditName = node.Name;
            }
            finally
            {
                _suppressWatcherEvents = false;
            }
        }

        private static bool IsNodeInCollection(FileNodeViewModel node, IEnumerable<FileNodeViewModel> collection)
        {
            var current = node;
            while (current.Parent != null)
            {
                current = current.Parent;
            }
            return collection.Contains(current);
        }

        private FileNodeViewModel? FindNodeByAbsolutePath(IEnumerable<FileNodeViewModel> nodes, string path)
        {
            foreach (var n in nodes)
            {
                if (n.AbsolutePath.Equals(path, StringComparison.OrdinalIgnoreCase)) return n;
                var found = FindNodeByAbsolutePath(n.Children, path);
                if (found != null) return found;
            }
            return null;
        }

        private FileNodeViewModel? ResolveTrackNode(FileNodeViewModel? selected)
        {
            if (selected == null) return null;
            if (selected.IsTrack && selected.VirtualPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                return selected;
            }

            if (selected.IsTrack || selected.IsDirectory || selected.IsArchive)
            {
                string baseName = Path.GetFileNameWithoutExtension(selected.Name);
                var trackChild = selected.Children.FirstOrDefault(c => c.VirtualPath.EndsWith($"{baseName.ToLower()}.txt", StringComparison.OrdinalIgnoreCase))
                              ?? selected.Children.FirstOrDefault(c => c.IsTrack && c.VirtualPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

                if (trackChild != null) return trackChild;
            }

            return selected.IsTrack ? selected : null;
        }

        public void QuickConvertSelectedTrack()
        {
            var targetNode = ResolveTrackNode(SelectedSourceNode);
            if (targetNode == null)
            {
                LogSession("Quick Convert: Selected item or folder does not contain a valid track descriptor");
                return;
            }

            string trackName = TrackDiscovery.GetBaseTrackName(Path.GetFileNameWithoutExtension(targetNode.Name));
            string trackTxtPath = targetNode.VirtualPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ? targetNode.VirtualPath
                : $"tracks/{trackName.ToLower()}/{trackName.ToLower()}.txt";

            var variants = TrackDiscoveryService.DiscoverVariants(trackName, _vfs, _sourceRootPath);

            var defaultVm = new ConvertTrackModalViewModel
            {
                TrackName = trackName,
                TrackTxtPath = trackTxtPath,
                OutputDirectory = Path.Combine(_destinationRootPath, trackName),
                AvailableVariants = variants,
                SelectedVariant = variants.FirstOrDefault() ?? "All Variants (Base + Race + Mission)",
                ExportObj = true,
                ExportSceneJson = true
            };

            LogSession($"[Quick Convert] Launching default export for '{trackName}'...");
            ExecuteTrackExport(defaultVm);
        }

        public async Task RebuildArchiveAsync(FileNodeViewModel? archiveNode)
        {
            if (archiveNode == null || !archiveNode.IsArchive || string.IsNullOrEmpty(archiveNode.AbsolutePath)) return;
            string pakPath = archiveNode.AbsolutePath;
            string dirPath = Path.ChangeExtension(pakPath, ".dir");
            if (!File.Exists(pakPath) || !File.Exists(dirPath)) return;

            SetBusy(true, $"Rebuilding & Defragmenting '{archiveNode.Name}'...");
            LogSession($"[+] Starting defragmentation of '{archiveNode.Name}'...");

            await Task.Run(() => {
                var idx = TDRArchive.ParseTrieIndex(dirPath);
                var newIdx = new List<FileEntry>();
                string tmpPak = pakPath + ".tmp";
                string tmpDir = dirPath + ".tmp";
                try
                {
                    using (var oldP = File.OpenRead(pakPath))
                    using (var newP = File.Create(tmpPak))
                    {
                        byte[] buffer = new byte[65536];
                        for (int i = 0; i < idx.Count; i++)
                        {
                            var f = idx[i];
                            oldP.Seek(f.Offset, SeekOrigin.Begin);

                            long startPos = newP.Position;
                            int pad = (int)((4 - (startPos % 4)) % 4);
                            if (pad > 0) newP.Write(new byte[pad], 0, pad);

                            long alignedPos = newP.Position;
                            long remaining = f.Size;
                            while (remaining > 0)
                            {
                                int read = oldP.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                                if (read <= 0) break;
                                newP.Write(buffer, 0, read);
                                remaining -= read;
                            }

                            newIdx.Add(new FileEntry
                            {
                                Name = f.Name,
                                Offset = (uint)alignedPos,
                                Size = f.Size
                            });
                        }
                    }

                    // Write new .DIR index to temp first
                    byte[] newDirBytes = TDRArchive.SerializeTrieIndex(newIdx);
                    File.WriteAllBytes(tmpDir, newDirBytes);

                    // Atomic Move: only overwrite original files once BOTH temp files are fully written
                    if (File.Exists(pakPath)) File.SetAttributes(pakPath, FileAttributes.Normal);
                    if (File.Exists(dirPath)) File.SetAttributes(dirPath, FileAttributes.Normal);

                    File.Move(tmpPak, pakPath, overwrite: true);
                    File.Move(tmpDir, dirPath, overwrite: true);

                    LogSession($"[+] Rebuild & Defragmentation completed for '{archiveNode.Name}' ({newIdx.Count} active files).");
                }
                catch (Exception ex)
                {
                    if (File.Exists(tmpPak)) try { File.Delete(tmpPak); } catch { }
                    if (File.Exists(tmpDir)) try { File.Delete(tmpDir); } catch { }
                    LogSession($"[ERROR] Failed to rebuild archive '{archiveNode.Name}': {ex.Message}");
                }
            });

            await ReindexAllAsync();
            SetBusy(false);
        }

        public async Task OpenConvertModalForTrackAsync(FileNodeViewModel? overrideNode = null)
        {
            if (IsBusy)
            {
                LogSession("[!] An export/extract is already running — please wait for it to finish before starting another.");
                return;
            }

            var targetNode = ResolveTrackNode(overrideNode ?? SelectedSourceNode);
            if (targetNode == null)
            {
                LogSession("Convert Track: Selected item or folder does not contain a valid track descriptor");
                return;
            }

            string selectedNodeNoExt = Path.GetFileNameWithoutExtension(targetNode.Name);
            string trackName = TrackDiscovery.GetBaseTrackName(selectedNodeNoExt);
            string trackTxtPath = targetNode.VirtualPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ? targetNode.VirtualPath
                : $"tracks/{trackName.ToLower()}/{trackName.ToLower()}.txt";

            var variants = TrackDiscoveryService.DiscoverVariants(trackName, _vfs, _sourceRootPath);

            string initialVariant = ConvertTrackModalViewModel.PresetAllSupported;
            if (selectedNodeNoExt.Contains("Race", StringComparison.OrdinalIgnoreCase) ||
                selectedNodeNoExt.Contains("Mission", StringComparison.OrdinalIgnoreCase))
            {
                var matched = variants.FirstOrDefault(v => v.Equals(selectedNodeNoExt, StringComparison.OrdinalIgnoreCase));
                if (matched != null) initialVariant = matched;
            }

            string? variantSuffix = GetVariantSuffix(initialVariant, trackName);
            string? resolvedNow = TrackExportPipeline.ResolveTrackDescriptor(_vfs, trackName, variantSuffix);

            var modalVm = new ConvertTrackModalViewModel
            {
                TrackName = trackName,
                TrackTxtPath = trackTxtPath,
                ResolvedDescriptorPath = resolvedNow ?? string.Empty,
                OutputDirectory = Path.Combine(_destinationRootPath, trackName),
                AvailableVariants = variants,
                SelectedVariant = initialVariant
            };

            PopulateHieTreeForModal(modalVm, trackName);

            modalVm.RequestStartExport = (vm) => ExecuteTrackExport(vm);

            LogSession($"[+] Opening Export Modal for track '{trackName}' (Resolved VFS Descriptor: '{resolvedNow ?? trackTxtPath}')");

            if (RequestShowConvertModal != null)
            {
                // Awaited so callers (e.g. the multi-node drop loop in MainWindow.axaml.cs)
                // don't move on to the next node while this modal is still open.
                await RequestShowConvertModal(modalVm);
            }
        }

        private string? GetVariantSuffix(string rawVariant, string trackName)
        {
            if (string.IsNullOrEmpty(rawVariant)) return null;
            string tName = trackName.ToLowerInvariant();
            string selLower = rawVariant.ToLowerInvariant();
            if (selLower == tName) return null;
            if (selLower.StartsWith(tName + "_", StringComparison.Ordinal))
                return rawVariant.Substring(tName.Length + 1);
            if (selLower.StartsWith(tName, StringComparison.Ordinal))
                return rawVariant.Substring(tName.Length).TrimStart('_');
            return rawVariant;
        }

        private async void ExecuteTrackExport(ConvertTrackModalViewModel vm)
        {
            try
            {
                if (IsBusy)
                {
                    LogSession("[!] An export/extract is already running — please wait for it to finish before starting another.");
                    return;
                }

                string allVariantsSentinel = ConvertTrackModalViewModel.PresetAllSupported;
                bool isAllVariants = string.IsNullOrEmpty(vm.SelectedVariant) ||
                                     vm.SelectedVariant.Equals(allVariantsSentinel, StringComparison.OrdinalIgnoreCase);

                var options = new TrackExportOptions(
                    ExportObj: vm.ExportObj,
                    ExportGltf: vm.ExportGltf,
                    ExportPngTextures: vm.ExportPngTextures,
                    IncludeMovableProps: vm.IncludeMovableProps,
                    ExportSceneJson: vm.ExportSceneJson,
                    NoMaterials: false,
                    UseLocalCoords: vm.UseLocalCoords,
                    UseGrouping: vm.UseGrouping,
                    DumpAll: vm.DumpAll,
                    Verbose: vm.VerboseLog,
                    EnableGroundSnap: vm.EnableGroundSnap,
                    SelectedHieFiles: vm.GetSelectedHiePaths()
                );

                SetBusy(true, $"Exporting track '{vm.TrackName}'...");
                ReportProgress(5, $"Starting export for '{vm.TrackName}'...");

                _suppressWatcherEvents = true;
                try
                {
                    await Task.Run(() =>
                    {
                        List<string> targetVariants;
                        string sel = vm.SelectedVariant ?? allVariantsSentinel;

                        if (sel.Equals(allVariantsSentinel, StringComparison.OrdinalIgnoreCase) || sel.StartsWith("All Variants", StringComparison.OrdinalIgnoreCase))
                        {
                            targetVariants = vm.AvailableVariants
                                .Where(v => !v.StartsWith("All ", StringComparison.OrdinalIgnoreCase) && !v.StartsWith("Base Track", StringComparison.OrdinalIgnoreCase) && !v.Equals(ConvertTrackModalViewModel.PresetCustom, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            if (targetVariants.Count == 0) targetVariants.Add(vm.TrackName);
                        }
                        else if (sel.StartsWith("Base Track Only", StringComparison.OrdinalIgnoreCase))
                        {
                            targetVariants = new List<string> { vm.TrackName };
                        }
                        else if (sel.StartsWith("All Races", StringComparison.OrdinalIgnoreCase))
                        {
                            targetVariants = vm.AvailableVariants
                                .Where(v => v.Contains("race", StringComparison.OrdinalIgnoreCase) || v.Equals(vm.TrackName, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                        }
                        else if (sel.StartsWith("All Missions", StringComparison.OrdinalIgnoreCase))
                        {
                            targetVariants = vm.AvailableVariants
                                .Where(v => v.Contains("mission", StringComparison.OrdinalIgnoreCase) || v.Equals(vm.TrackName, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                        }
                        else if (sel.Equals(ConvertTrackModalViewModel.PresetCustom, StringComparison.OrdinalIgnoreCase))
                        {
                            // Custom Selection: only export layer variants whose root node has at least
                            // one selected HIE file. VirtualPath of layer root nodes matches the format
                            // expected by GetVariantSuffix (e.g. "Hollowood", "Hollowood_Race1").
                            targetVariants = vm.HieTreeNodes
                                .Where(n => n.IsDirectory && n.IsSelected)
                                .Select(n => n.VirtualPath)
                                .ToList();
                            if (targetVariants.Count == 0) targetVariants.Add(vm.TrackName);
                        }
                        else
                        {
                            targetVariants = new List<string> { sel };
                        }

                        for (int i = 0; i < targetVariants.Count; i++)
                        {
                            var variant = targetVariants[i];
                            double macroP = 10.0 + ((double)i / targetVariants.Count) * 80.0;
                            ReportProgress(macroP, $"Exporting variant layer ({i + 1}/{targetVariants.Count}): {variant}");

                            string? suffix = GetVariantSuffix(variant, vm.TrackName);
                            TrackExportPipeline.ExportTrack(_vfs, vm.TrackName, suffix, vm.OutputDirectory, options, LogSession, (subPct, subMsg) =>
                            {
                                ReportSubProgress(subPct, subMsg);
                            });
                        }
                        ReportProgress(100, $"Completed export for '{vm.TrackName}'");
                    });
                }
                catch (Exception ex)
                {
                    LogSession($"[ERROR] Track export failed: {ex.Message}");
                    LogSession($"[TRACE] {ex}");
                }
                finally
                {
                    IsBusy = false;
                    RefreshDestinationTree();
                    _suppressWatcherEvents = false;
                }
            }
            catch (Exception ex)
            {
                LogSession($"[CRITICAL ERROR] Unhandled exception in ExecuteTrackExport: {ex}");
            }
        }

        private void PopulateHieTreeForModal(ConvertTrackModalViewModel modalVm, string cleanName)
        {
            string tPrefix = cleanName.ToLowerInvariant();
            string trackFolderPrefix = $"tracks/{tPrefix}";

            var matchingFiles = _vfs.GetFiles()
                .Where(f => f.Name.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                .Where(f => {
                    string normName = f.Name.Replace('\\', '/').ToLowerInvariant();
                    string normArchive = (f.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant();
                    string fileName = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();

                    bool inTrackFolder = normName.StartsWith(trackFolderPrefix + "/") ||
                                         normName.StartsWith(trackFolderPrefix + "_") ||
                                         normArchive.Contains(trackFolderPrefix + "/") ||
                                         normArchive.Contains(trackFolderPrefix + "_");

                    bool startsWithName = fileName.StartsWith(tPrefix, StringComparison.OrdinalIgnoreCase);

                    return inTrackFolder || startsWithName;
                })
                .ToList();

            if (matchingFiles.Count == 0)
            {
                matchingFiles = _vfs.GetFiles()
                    .Where(f => f.Name.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // 1st tier: Layer Root nodes (e.g. Hollowood, Hollowood_Race1, Hollowood_Mission1)
            var layerRootNodes = new Dictionary<string, HieNodeViewModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in matchingFiles)
            {
                string path = file.Name.Replace('\\', '/');
                string fileName = Path.GetFileName(path);
                string fLower = fileName.ToLowerInvariant();
                string pathLower = path.ToLowerInvariant();
                string archiveLower = (file.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant();

                // 1. Determine physical layer root (Hollowood, Hollowood_Race1, Hollowood_Mission1, etc.)
                string layerRootKey = cleanName;
                if (fLower.Contains("race1") || pathLower.Contains("race1") || archiveLower.Contains("race1"))
                    layerRootKey = $"{cleanName}_Race1";
                else if (fLower.Contains("race2") || pathLower.Contains("race2") || archiveLower.Contains("race2"))
                    layerRootKey = $"{cleanName}_Race2";
                else if (fLower.Contains("race3") || pathLower.Contains("race3") || archiveLower.Contains("race3"))
                    layerRootKey = $"{cleanName}_Race3";
                else if (fLower.Contains("mission1") || pathLower.Contains("mission1") || archiveLower.Contains("mission1"))
                    layerRootKey = $"{cleanName}_Mission1";
                else if (fLower.Contains("mission2") || pathLower.Contains("mission2") || archiveLower.Contains("mission2"))
                    layerRootKey = $"{cleanName}_Mission2";

                if (!layerRootNodes.TryGetValue(layerRootKey, out var layerRootNode))
                {
                    string displayLayerName = FormatLayerDisplayName(layerRootKey, cleanName);

                    layerRootNode = new HieNodeViewModel
                    {
                        Name = displayLayerName,
                        VirtualPath = layerRootKey,
                        IsDirectory = true,
                        IsSelected = true,
                        ShowTopSeparator = modalVm.HieTreeNodes.Count > 0,
                        NodeType = "TrackLayerRoot",
                        OnSelectionChangedCallback = () => modalVm.NotifyUserTreeToggled()
                    };
                    layerRootNodes[layerRootKey] = layerRootNode;
                    modalVm.HieTreeNodes.Add(layerRootNode);
                }

                // 2. Determine physical VFS subfolder inside this layer
                string rawDir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                string displaySubfolder = rawDir;

                if (displaySubfolder.StartsWith("tracks/", StringComparison.OrdinalIgnoreCase))
                    displaySubfolder = displaySubfolder.Substring("tracks/".Length);
                else if (displaySubfolder.StartsWith("assets/tracks/", StringComparison.OrdinalIgnoreCase))
                    displaySubfolder = displaySubfolder.Substring("assets/tracks/".Length);

                // Strip root layer prefix if subfolder repeats track name (e.g. Hollowood/Level Convsoft -> Level Convsoft)
                if (displaySubfolder.StartsWith(cleanName + "/", StringComparison.OrdinalIgnoreCase))
                    displaySubfolder = displaySubfolder.Substring(cleanName.Length + 1);

                HieNodeViewModel parentFolderNode = layerRootNode;

                if (!string.IsNullOrWhiteSpace(displaySubfolder) && !displaySubfolder.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                {
                    string folderKey = $"{layerRootKey}/{displaySubfolder}";
                    var existingSub = layerRootNode.Children.FirstOrDefault(c => c.VirtualPath.Equals(folderKey, StringComparison.OrdinalIgnoreCase));
                    if (existingSub == null)
                    {
                        existingSub = new HieNodeViewModel
                        {
                            Name = displaySubfolder,
                            VirtualPath = folderKey,
                            IsDirectory = true,
                            IsSelected = true,
                            NodeType = "VfsSubfolder",
                            Parent = layerRootNode,
                            OnSelectionChangedCallback = () => modalVm.NotifyUserTreeToggled()
                        };
                        layerRootNode.Children.Add(existingSub);
                    }
                    parentFolderNode = existingSub;
                }

                bool isBlacklistedDefault = fileName.Contains("skybox", StringComparison.OrdinalIgnoreCase) ||
                                            fileName.Contains("billboard", StringComparison.OrdinalIgnoreCase) ||
                                            fileName.Contains("campaths", StringComparison.OrdinalIgnoreCase) ||
                                            fileName.Contains("intpaths", StringComparison.OrdinalIgnoreCase) ||
                                            fileName.Contains("zoomin", StringComparison.OrdinalIgnoreCase) ||
                                            fileName.Contains("look", StringComparison.OrdinalIgnoreCase);

                var fileNode = new HieNodeViewModel
                {
                    Name = fileName,
                    VirtualPath = file.Name,
                    IsDirectory = false,
                    IsSelected = !isBlacklistedDefault,
                    NodeType = "MeshFile",
                    Parent = parentFolderNode,
                    OnSelectionChangedCallback = () => modalVm.NotifyUserTreeToggled()
                };
                parentFolderNode.Children.Add(fileNode);
            }
        }

        private static string FormatLayerDisplayName(string layerKey, string cleanName)
        {
            if (layerKey.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                return $"{cleanName} (Base Track)";

            if (layerKey.StartsWith(cleanName + "_", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = layerKey.Substring(cleanName.Length + 1);
                return $"{cleanName} ({suffix})";
            }

            return layerKey.Replace('_', ' ');
        }
    }
}
