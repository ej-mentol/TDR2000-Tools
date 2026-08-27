using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TDR.Tools.ViewModels;
using TDR.Tools.Views;

namespace TDR.Tools
{
    public partial class MainWindow : Window, IDisposable
    {
        private readonly MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;

            _vm.RequestShowConvertModal = async (modalVm) =>
            {
                var modal = new ConvertTrackWindow
                {
                    DataContext = modalVm
                };
                await modal.ShowDialog(this);
            };
            _vm.LogLines.CollectionChanged += (s, e) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_vm.LogLines.Count > 0)
                    {
                        LogListBox?.ScrollIntoView(_vm.LogLines.Count - 1);
                    }
                });
            };
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            _vm.InitializeStartup();

            SourceTreeView?.AddHandler(PointerPressedEvent, OnSourcePointerPressed, RoutingStrategies.Tunnel);
            SourceTreeView?.AddHandler(PointerMovedEvent, OnSourcePointerMoved, RoutingStrategies.Tunnel);

            DestinationGrid?.AddHandler(PointerPressedEvent, OnDestinationPointerPressed, RoutingStrategies.Tunnel);
            DestinationTreeView?.AddHandler(PointerPressedEvent, OnDestinationPointerPressed, RoutingStrategies.Tunnel);
            DestinationTreeView?.AddHandler(PointerMovedEvent, OnDestinationPointerMoved, RoutingStrategies.Tunnel);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Dispose();
        }

        public void Dispose()
        {
            _vm.Dispose();
            GC.SuppressFinalize(this);
        }

        private void OnClosePreviewClick(object? sender, RoutedEventArgs e)
        {
            _vm.Preview.IsPreviewDrawerExpanded = false;
            _vm.Preview.ClosePreview();
        }

        private async void OnSelectSourceFolderClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Source Folder or Track VFS Root",
                    AllowMultiple = false
                });

                if (folders.Count > 0)
                {
                    string path = folders[0].Path.LocalPath;
                    await _vm.IndexDirectory(path);
                }
            }
            catch (Exception ex)
            {
                _vm.LogSession($"[ERROR] OnSelectSourceFolderClick: {ex.Message}");
            }
        }

        private async void OnSelectDestinationFolderClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Output / Staging Folder",
                    AllowMultiple = false
                });

                if (folders.Count > 0)
                {
                    string path = folders[0].Path.LocalPath;
                    _vm.SetDestinationDirectory(path);
                }
            }
            catch (Exception ex)
            {
                _vm.LogSession($"[ERROR] OnSelectDestinationFolderClick: {ex.Message}");
            }
        }

        // --- NAVIGATION BUTTON HANDLERS ---
        private void OnSourceNavBackClick(object? sender, RoutedEventArgs e) => _vm.SourceNavigateBack();
        private void OnSourceNavForwardClick(object? sender, RoutedEventArgs e) => _vm.SourceNavigateForward();
        private void OnSourceNavUpClick(object? sender, RoutedEventArgs e) => _vm.SourceNavigateUp();

        private void OnDestinationNavBackClick(object? sender, RoutedEventArgs e) => _vm.DestinationNavigateBack();
        private void OnDestinationNavForwardClick(object? sender, RoutedEventArgs e) => _vm.DestinationNavigateForward();
        private void OnDestinationNavUpClick(object? sender, RoutedEventArgs e) => _vm.DestinationNavigateUp();

        // --- DRAG & DROP HANDLERS ---
        private FileNodeViewModel? _draggedNode = null;
        private bool _isRightClickDrag = false;
        private Avalonia.Point _dragStartPoint;
        private bool _isPointerDown = false;
        private PointerPressedEventArgs? _lastSourcePressedArgs = null;
        private PointerPressedEventArgs? _lastDestinationPressedArgs = null;
        // Guard against OnDestinationDrop firing twice: event bubbles from TreeView → DestinationGrid.
        private bool _isProcessingDrop = false;

        private void OnSourcePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Set explicitly on every press (not just via GotFocus) — a right-click alone doesn't
            // reliably raise GotFocus on a TreeView in Avalonia, which left the "active panel"
            // tracking stuck on whichever side was last left-clicked.
            _lastFocusedPanel = "Source";
            _lastSourcePressedArgs = e;

            Visual? sourceVisual = e.Source as Visual;
            if (e.Source is TextBox || sourceVisual?.GetVisualAncestors().OfType<TextBox>().Any() == true || (sourceVisual != null && IsScrollBarVisual(sourceVisual)))
            {
                _isPointerDown = false;
                return;
            }

            var point = e.GetCurrentPoint(SourceTreeView);
            var hitNode = (e.Source as Control)?.DataContext as FileNodeViewModel
                       ?? ((e.Source as Visual)?.GetVisualAncestors()
                           .OfType<TreeViewItem>()
                           .FirstOrDefault()?.DataContext as FileNodeViewModel);

            if (hitNode != null)
            {
                _vm.SelectedSourceNode = hitNode;
            }
            else
            {
                _vm.SelectedSourceNode = null;
                _vm.SelectedSourceNodes.Clear();
            }

            if (point.Properties.IsRightButtonPressed)
            {
                _dragStartPoint = point.Position;
                _isPointerDown = true;
                _isRightClickDrag = true;
            }
            else if (point.Properties.IsLeftButtonPressed)
            {
                _dragStartPoint = point.Position;
                _isPointerDown = true;
                _isRightClickDrag = false;
            }
        }

        private void OnSourceContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _lastFocusedPanel = "Source";

            var targetControl = sender as Control;
            var hitNode = targetControl?.DataContext as FileNodeViewModel
                       ?? (targetControl as Visual)?.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext as FileNodeViewModel
                       ?? (_lastSourcePressedArgs?.Source as Control)?.DataContext as FileNodeViewModel
                       ?? ((_lastSourcePressedArgs?.Source as Visual)?.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext as FileNodeViewModel);

            if (hitNode != null)
            {
                _vm.SelectedSourceNode = hitNode;
            }
            else
            {
                _vm.SelectedSourceNode = null;
                _vm.SelectedSourceNodes.Clear();
            }

            var node = _vm.SelectedSourceNode;
            if (node != null && (node.Name == ".." || node.AbsolutePath == ".." || node.Name.StartsWith("..")))
            {
                e.Cancel = true;
            }
        }

        private void OnDestinationContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _lastFocusedPanel = "Destination";

            var targetControl = sender as Control;
            var hitNode = targetControl?.DataContext as FileNodeViewModel
                       ?? (targetControl as Visual)?.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext as FileNodeViewModel
                       ?? (_lastDestinationPressedArgs?.Source as Control)?.DataContext as FileNodeViewModel
                       ?? ((_lastDestinationPressedArgs?.Source as Visual)?.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext as FileNodeViewModel);

            if (hitNode != null)
            {
                _vm.SelectedDestinationNode = hitNode;
            }
            else
            {
                _vm.SelectedDestinationNode = null;
                _vm.SelectedDestinationNodes.Clear();
            }

            var node = _vm.SelectedDestinationNode;
            if (node != null && (node.Name == ".." || node.AbsolutePath == ".." || node.Name.StartsWith("..")))
            {
                e.Cancel = true;
            }
        }

        private static bool IsScrollBarVisual(Visual visual)
        {
            Visual? current = visual;
            while (current != null)
            {
                if (current is Avalonia.Controls.Primitives.ScrollBar ||
                    current is Avalonia.Controls.Primitives.Thumb)
                {
                    return true;
                }
                current = current.GetVisualParent();
            }
            return false;
        }

        private async void OnSourcePointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isPointerDown || _lastSourcePressedArgs == null) return;
            if (e.Source is TextBox || (e.Source as Visual)?.GetVisualAncestors().OfType<TextBox>().Any() == true)
            {
                _isPointerDown = false;
                return;
            }

            var point = e.GetCurrentPoint(SourceTreeView);
            if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
            {
                _isPointerDown = false;
                return;
            }

            var diff = point.Position - _dragStartPoint;
            if (Math.Abs(diff.X) > 6 || Math.Abs(diff.Y) > 6)
            {
                _isPointerDown = false;
                if (_vm.SelectedSourceNode == null || _vm.SelectedSourceNode.Name == "..") return;

                _draggedNode = _vm.SelectedSourceNode;

                try
                {
                    var data = new DataTransfer();
                    await DragDrop.DoDragDropAsync(_lastSourcePressedArgs, data, DragDropEffects.Copy);
                }
                catch (Exception ex)
                {
                    _vm.LogSession($"[DragDrop Error] {ex.Message}");
                }
            }
        }

        private void OnSourceDragOver(object? sender, DragEventArgs e)
        {
            var targetPakNode = (e.Source as Control)?.DataContext as FileNodeViewModel;
            if (targetPakNode != null && (targetPakNode.IsArchive || targetPakNode.IsVirtual))
            {
                e.DragEffects = DragDropEffects.Copy;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
        }

        private async void OnSourceDrop(object? sender, DragEventArgs e)
        {
            var targetPakNode = (e.Source as Control)?.DataContext as FileNodeViewModel;
            if (targetPakNode == null || (!targetPakNode.IsArchive && !targetPakNode.IsVirtual))
            {
                _draggedNode = null;
                _isRightClickDrag = false;
                return;
            }

            // 1. Check dragged files from OS file manager (Explorer)
            var storageItems = e.DataTransfer?.TryGetFiles();
            List<string>? diskPaths = null;
            if (storageItems != null)
            {
                diskPaths = storageItems
                    .Select(f => f.Path.LocalPath)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();
            }

            // 2. Check internal VFS nodes (from Source or Destination tree)
            List<FileNodeViewModel>? vfsNodes = null;
            if (_draggedNode != null)
            {
                if (_vm.SelectedSourceNodes.Count > 0 && _vm.SelectedSourceNodes.Contains(_draggedNode))
                    vfsNodes = _vm.SelectedSourceNodes.ToList();
                else if (_vm.SelectedDestinationNodes.Count > 0 && _vm.SelectedDestinationNodes.Contains(_draggedNode))
                    vfsNodes = _vm.SelectedDestinationNodes.ToList();
                else
                    vfsNodes = new List<FileNodeViewModel> { _draggedNode };
            }

            if ((diskPaths == null || diskPaths.Count == 0) && (vfsNodes == null || vfsNodes.Count == 0))
            {
                _vm.LogSession($"[!] Drop onto '{targetPakNode.Name}' ignored: no valid files were dragged.");
            }
            else
            {
                await _vm.AddFilesToArchiveAsync(targetPakNode, fileOrFolderPathsOnDisk: diskPaths, vfsNodes: vfsNodes);
            }

            _draggedNode = null;
            _isRightClickDrag = false;
        }

        private async void OnAddFilesToArchiveClick(object? sender, RoutedEventArgs e)
        {
            var targetPakNode = GetActiveSelectedNode();
            if (targetPakNode == null || (!targetPakNode.IsArchive && !targetPakNode.IsVirtual)) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Add Files to Archive '{targetPakNode.Name}'",
                AllowMultiple = true
            });

            if (files != null && files.Count > 0)
            {
                var diskPaths = files.Select(f => f.Path.LocalPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
                await _vm.AddFilesToArchiveAsync(targetPakNode, diskPaths, vfsNodes: null);
            }
        }

        private void OnDestinationDragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = DragDropEffects.Copy;
        }

        private async void OnPackFolderToPakClick(object? sender, RoutedEventArgs e)
        {
            var targetNode = GetActiveSelectedNode();
            if (targetNode != null && targetNode.IsDirectory && !string.IsNullOrEmpty(targetNode.AbsolutePath) && Directory.Exists(targetNode.AbsolutePath))
            {
                string inputFolder = targetNode.AbsolutePath;
                string parentDir = Path.GetDirectoryName(inputFolder) ?? inputFolder;
                string folderName = Path.GetFileName(inputFolder);
                string outputPak = Path.Combine(parentDir, $"{folderName}.pak");

                _vm.PackFolderToPak(inputFolder, outputPak, compress: true);
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select Folder to Pack into .PAK Archive",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                string inputFolder = folders[0].Path.LocalPath;
                var saveFile = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Save Output .PAK Archive",
                    SuggestedFileName = $"{Path.GetFileName(inputFolder)}.pak",
                    DefaultExtension = "pak"
                });

                if (saveFile != null)
                {
                    string outputPak = saveFile.Path.LocalPath;
                    _vm.PackFolderToPak(inputFolder, outputPak, compress: true);
                }
            }
        }

        private async void OnSettingsMenuClick(object? sender, RoutedEventArgs e)
        {
            var dialog = new Views.SettingsWindow();
            bool saved = await dialog.ShowDialog<bool>(this);
            if (saved)
            {
                // Settings like "show .dir index files" affect how the trees are built,
                // not just future actions — rebuild both so the change is visible immediately.
                _vm.RefreshSourceTree();
                _vm.RefreshDestinationTree();
            }
        }

        private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
        private void OnExtractSelectedClick(object? sender, RoutedEventArgs e) => _vm.ExtractSelected();
        private void OnDeleteSelectedClick(object? sender, RoutedEventArgs e) => PerformDelete(permanent: false);
        private void OnTogglePreviewClick(object? sender, RoutedEventArgs e) => _vm.Preview.IsPreviewDrawerExpanded = !_vm.Preview.IsPreviewDrawerExpanded;

        private void OnAboutMenuClick(object? sender, RoutedEventArgs e)
        {
            _vm.LogSession("=== TDR2000 Tools (dev build) ===");
            _vm.LogSession("Carmageddon TDR 2000 Asset & Track Management Suite.");
            _vm.LogSession("Features: PAK/DIR trie repacker, .hie/.msh 3D OBJ exporter, scene.json generator.");
        }

        private async void OnDestinationDrop(object? sender, DragEventArgs e)
        {
            // Prevent double-fire: Drop event bubbles from TreeView up to DestinationGrid,
            // causing this handler to run twice per drag. Second call is a no-op.
            if (_isProcessingDrop) { e.Handled = true; return; }
            _isProcessingDrop = true;
            try
            {
            var nodesToProcess = (_vm.SelectedSourceNodes.Count > 0
                ? _vm.SelectedSourceNodes.ToList()
                : (_draggedNode != null ? new List<FileNodeViewModel> { _draggedNode }
                : (_vm.SelectedSourceNode != null ? new List<FileNodeViewModel> { _vm.SelectedSourceNode } : new List<FileNodeViewModel>())));

            if (nodesToProcess.Count == 0) return;

            if (_vm.IsBusy)
            {
                _vm.LogSession("[!] An export/extract is already running — please wait for it to finish.");
                _draggedNode = null;
                _isRightClickDrag = false;
                return;
            }

            if (string.IsNullOrEmpty(_vm.DestinationRootPath))
            {
                string defaultExportDir = Path.Combine(Directory.GetCurrentDirectory(), "EXPORT");
                Directory.CreateDirectory(defaultExportDir);
                _vm.SetDestinationDirectory(defaultExportDir);
            }

            bool isRightClick = _isRightClickDrag;

            if (isRightClick)
            {
                var validNodes = nodesToProcess.Where(n => n.Name != "..").ToList();
                if (validNodes.Count > 0)
                {
                    ShowOnDropContextMenu(validNodes, sender as Control);
                }
                _draggedNode = null;
                _isRightClickDrag = false;
                return;
            }

            var hitControl = sender as Control;
            var targetNode = hitControl?.DataContext as FileNodeViewModel
                          ?? (hitControl as Visual)?.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext as FileNodeViewModel;

            // Scenario 3: Drop files onto a PAK archive in Destination panel -> pack/add files into that archive
            if (targetNode != null && (targetNode.IsArchive || targetNode.IsVirtual))
            {
                var validNodes = nodesToProcess.Where(n => n.Name != "..").ToList();
                if (validNodes.Count > 0)
                {
                    var vfsSourceNodes = validNodes.Where(n => n.IsVirtual).ToList();
                    var diskPaths = validNodes.Where(n => !n.IsVirtual && !string.IsNullOrEmpty(n.AbsolutePath)).Select(n => n.AbsolutePath).ToList();
                    await _vm.AddFilesToArchiveAsync(targetNode, diskPaths, vfsSourceNodes.Count > 0 ? vfsSourceNodes : null);
                }
                _draggedNode = null;
                _isRightClickDrag = false;
                return;
            }

            string targetDir = _vm.DestinationRootPath;
            if (targetNode != null && targetNode.IsDirectory && targetNode.Name != ".." && !string.IsNullOrEmpty(targetNode.AbsolutePath) && Directory.Exists(targetNode.AbsolutePath))
            {
                targetDir = targetNode.AbsolutePath;
            }

            foreach (var node in nodesToProcess)
            {
                if (node.Name == "..") continue;

                bool isPakFile = !node.IsDirectory && node.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);
                if (node.IsDirectory || node.IsArchive || isPakFile)
                {
                    var settings = Services.AppSettings.Load();
                    string action = settings.RememberPakDragAction ? settings.PakDragAction : "Ask";

                    if (action == "Ask")
                    {
                        bool isFolder = node.IsDirectory;
                        bool containsPakFiles = false;

                        if (isFolder && !string.IsNullOrEmpty(node.AbsolutePath) && Directory.Exists(node.AbsolutePath))
                        {
                            containsPakFiles = Directory.GetFiles(node.AbsolutePath, "*.pak", SearchOption.AllDirectories).Length > 0;
                        }
                        else if (!isFolder && node.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                        {
                            containsPakFiles = true;
                        }

                        // If folder has NO .pak files at all, bypass prompt and open ConvertTrackWindow directly
                        if (isFolder && !containsPakFiles)
                        {
                            await _vm.OpenConvertModalForTrackAsync(node);
                        }
                        else
                        {
                            var dialog = new Views.PakDragDropActionWindow(node.Name, isFolder, containsPakFiles, isTrack: node.IsTrack);
                            var userChoice = await dialog.ShowDialog<Views.PakUserAction>(this);

                            if (userChoice == Views.PakUserAction.Extract)
                            {
                                if (dialog.RememberChoice)
                                {
                                    settings.PakDragAction = "Extract";
                                    settings.RememberPakDragAction = true;
                                    settings.Save();
                                }
                                _vm.ExtractNodeToDestination(node, createSubfolderForPak: dialog.CreateSubfolder, flatFiles: dialog.FlatFiles, unpackOnly: dialog.UnpackOnly, customTargetDir: targetDir);
                            }
                            else if (userChoice == Views.PakUserAction.Convert)
                            {
                                if (dialog.RememberChoice)
                                {
                                    settings.PakDragAction = "Convert";
                                    settings.RememberPakDragAction = true;
                                }
                                settings.Save();
                                await _vm.OpenConvertModalForTrackAsync(node);
                            }
                        }
                    }
                    else if (action == "Extract")
                    {
                        _vm.ExtractNodeToDestination(node, createSubfolderForPak: true, flatFiles: false, customTargetDir: targetDir);
                    }
                    else if (action == "Convert")
                    {
                        await _vm.OpenConvertModalForTrackAsync(node);
                    }
                }
                else
                {
                    _vm.ExtractNodeToDestination(node, createSubfolderForPak: false, flatFiles: false, customTargetDir: targetDir);
                }
            }

            _draggedNode = null;
            _isRightClickDrag = false;
            } // end try (_isProcessingDrop guard)
            finally
            {
                _isProcessingDrop = false;
            }
        }

        private void ShowOnDropContextMenu(List<FileNodeViewModel> nodes, Control? targetControl)
        {
            if (nodes.Count == 0) return;

            var contextMenu = new ContextMenu();
            string label = nodes.Count == 1 ? $"'{nodes[0].Name}'" : $"{nodes.Count} items";

            bool containsPaks = nodes.Any(n => n.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase));
            string verb = containsPaks ? "Unpack" : "Copy";

            var extractToSubfolderItem = new MenuItem
            {
                Header = containsPaks ? $"📦 Unpack {label} (Subfolders for Archives)" : $"📂 Copy {label} to Destination"
            };
            extractToSubfolderItem.Click += (s, e) =>
            {
                foreach (var n in nodes) _vm.ExtractNodeToDestination(n, createSubfolderForPak: true, flatFiles: false);
            };
            contextMenu.Items.Add(extractToSubfolderItem);

            var extractFlatItem = new MenuItem
            {
                Header = containsPaks ? $"📄 Unpack {label} Flat Here" : $"📄 Copy {label} Flat Here"
            };
            extractFlatItem.Click += (s, e) =>
            {
                foreach (var n in nodes) _vm.ExtractNodeToDestination(n, createSubfolderForPak: false, flatFiles: true);
            };
            contextMenu.Items.Add(extractFlatItem);

            contextMenu.Items.Add(new Separator());

            var cancelItem = new MenuItem { Header = "❌ Cancel" };
            contextMenu.Items.Add(cancelItem);

            if (targetControl != null)
            {
                contextMenu.Open(targetControl);
            }
        }

        // --- KEYBOARD & DELETION HANDLERS ---
        private string _lastFocusedPanel = "Source";

        private void OnSourceTreeGotFocus(object? sender, RoutedEventArgs e) => _lastFocusedPanel = "Source";
        private void OnDestinationTreeGotFocus(object? sender, RoutedEventArgs e) => _lastFocusedPanel = "Destination";

        private void OnDestinationPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _lastFocusedPanel = "Destination";
            _lastDestinationPressedArgs = e;

            Visual? destVisual = e.Source as Visual;
            if (e.Source is TextBox || destVisual?.GetVisualAncestors().OfType<TextBox>().Any() == true || (destVisual != null && IsScrollBarVisual(destVisual))) return;

            var tree = DestinationTreeView ?? (sender as Visual);
            var point = e.GetCurrentPoint(tree);
            var hitNode = (e.Source as Control)?.DataContext as FileNodeViewModel
                       ?? ((e.Source as Visual)?.GetVisualAncestors()
                           .OfType<TreeViewItem>()
                           .FirstOrDefault()?.DataContext as FileNodeViewModel);

            if (hitNode != null)
            {
                _vm.SelectedDestinationNode = hitNode;
            }
            else
            {
                _vm.SelectedDestinationNode = null;
                _vm.SelectedDestinationNodes.Clear();
            }

            if (point.Properties.IsRightButtonPressed)
            {
                _dragStartPoint = point.Position;
                _isPointerDown = true;
                _isRightClickDrag = true;
            }
            else if (point.Properties.IsLeftButtonPressed)
            {
                _dragStartPoint = point.Position;
                _isPointerDown = true;
                _isRightClickDrag = false;
            }
        }

        private async void OnDestinationPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isPointerDown || _lastDestinationPressedArgs == null) return;
            if (e.Source is TextBox || (e.Source as Visual)?.GetVisualAncestors().OfType<TextBox>().Any() == true)
            {
                _isPointerDown = false;
                return;
            }

            var tree = DestinationTreeView;
            if (tree == null) return;

            var point = e.GetCurrentPoint(tree);
            if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
            {
                _isPointerDown = false;
                return;
            }

            var diff = point.Position - _dragStartPoint;
            if (Math.Abs(diff.X) > 6 || Math.Abs(diff.Y) > 6)
            {
                _isPointerDown = false;
                if (_vm.SelectedDestinationNode == null || _vm.SelectedDestinationNode.Name == "..") return;

                _draggedNode = _vm.SelectedDestinationNode;

                try
                {
                    var data = new DataTransfer();
                    await DragDrop.DoDragDropAsync(_lastDestinationPressedArgs, data, DragDropEffects.Copy);
                }
                catch (Exception ex)
                {
                    _vm.LogSession($"[DragDrop Error] {ex.Message}");
                }
            }
        }

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Source is TextBox || (e.Source as Visual)?.GetVisualAncestors().OfType<TextBox>().Any() == true)
            {
                return;
            }

            if (e.Key == Key.Tab)
            {
                if (_lastFocusedPanel == "Source")
                {
                    _lastFocusedPanel = "Destination";
                    DestinationTreeView?.Focus();
                }
                else
                {
                    _lastFocusedPanel = "Source";
                    SourceTreeView?.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Back)
            {
                if (_lastFocusedPanel == "Destination")
                {
                    _vm.DestinationNavigateUp();
                }
                else
                {
                    _vm.SourceNavigateUp();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (_lastFocusedPanel == "Destination" && _vm.SelectedDestinationNode != null)
                {
                    _vm.NavigateIntoDestinationNode(_vm.SelectedDestinationNode);
                    e.Handled = true;
                }
                else if (_lastFocusedPanel == "Source" && _vm.SelectedSourceNode != null)
                {
                    OnOpenNodeClick(sender, e);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.F5)
            {
                if (_lastFocusedPanel == "Destination")
                {
                    OnRefreshDestinationClick(sender, e);
                }
                else
                {
                    OnRefreshSourceClick(sender, e);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.F6)
            {
                if (_lastFocusedPanel == "Source")
                {
                    _vm.LogSession("[F6 Guard] Move from Source is prohibited to prevent damaging game installation. Use F5 Copy.");
                }
                else
                {
                    _vm.LogSession("[F6 Move] Destination sandbox item move.");
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                PerformDelete(isShift);
                e.Handled = true;
            }
            else if (e.Key == Key.F2)
            {
                OnRenameNodeClick(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                OnPropertiesClick(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _vm.Preview.IsPreviewDrawerExpanded = !_vm.Preview.IsPreviewDrawerExpanded;
                e.Handled = true;
            }
        }

        private FileNodeViewModel? GetActiveSelectedNode()
        {
            if (_lastFocusedPanel == "Destination")
            {
                return _vm.SelectedDestinationNodes.FirstOrDefault() ?? _vm.SelectedDestinationNode;
            }
            return _vm.SelectedSourceNodes.FirstOrDefault() ?? _vm.SelectedSourceNode;
        }

        private async void OnCreateNewPakClick(object? sender, RoutedEventArgs e)
        {
            var parentNode = GetActiveSelectedNode();
            await _vm.CreateNewPakArchiveAsync(parentNode);
        }

        private void OnRenameNodeClick(object? sender, RoutedEventArgs e)
        {
            var node = GetTargetNode(sender);

            if (node != null && (node.IsVirtual || (!string.IsNullOrEmpty(node.AbsolutePath) && (System.IO.File.Exists(node.AbsolutePath) || System.IO.Directory.Exists(node.AbsolutePath)))))
            {
                node.IsEditing = true;
            }
        }

        private void OnRenameTextBoxLoaded(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.BringIntoView();
                textBox.Focus();
                string text = textBox.Text ?? string.Empty;
                int extIndex = text.LastIndexOf('.');
                if (extIndex > 0)
                {
                    textBox.SelectionStart = 0;
                    textBox.SelectionEnd = extIndex;
                }
                else
                {
                    textBox.SelectAll();
                }
            }
        }

        private async void OnRenameTextBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is TextBox textBox && textBox.DataContext is FileNodeViewModel node)
            {
                if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    textBox.SelectAll();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Enter)
                {
                    string newName = textBox.Text ?? node.Name;
                    if (!_vm.RenameNode(node, newName, out string errorMsg))
                    {
                        e.Handled = true;
                        await MessageAlertWindow.ShowAsync(this, "Cannot Rename", errorMsg);
                        node.IsEditing = true;
                        textBox.Focus();
                        return;
                    }

                    node.IsEditing = false;
                    e.Handled = true;
                    if (_lastFocusedPanel == "Destination") DestinationGrid?.Focus();
                    else SourceTreeView?.Focus();
                }
                else if (e.Key == Key.Escape)
                {
                    node.EditName = node.Name;
                    node.IsEditing = false;
                    e.Handled = true;
                    if (_lastFocusedPanel == "Destination") DestinationGrid?.Focus();
                    else SourceTreeView?.Focus();
                }
            }
        }

        private void OnRenameTextBoxLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.DataContext is FileNodeViewModel node && node.IsEditing)
            {
                string newName = textBox.Text ?? node.Name;
                node.IsEditing = false;
                _vm.RenameNode(node, newName);
            }
        }

        private void OnDeleteNodeClick(object? sender, RoutedEventArgs e)
        {
            PerformDelete(permanent: false);
        }

        private void OnPermanentDeleteSelectedClick(object? sender, RoutedEventArgs e)
        {
            PerformDelete(permanent: true);
        }

        private void OnSourceDeleteNodeClick(object? sender, RoutedEventArgs e)
        {
            _lastFocusedPanel = "Source";
            var itemNode = (sender as MenuItem)?.DataContext as FileNodeViewModel;
            PerformDeleteForPanel("Source", permanent: false, itemNode);
        }

        private void OnSourcePermanentDeleteNodeClick(object? sender, RoutedEventArgs e)
        {
            _lastFocusedPanel = "Source";
            var itemNode = (sender as MenuItem)?.DataContext as FileNodeViewModel;
            PerformDeleteForPanel("Source", permanent: true, itemNode);
        }

        private void OnDestinationDeleteNodeClick(object? sender, RoutedEventArgs e)
        {
            _lastFocusedPanel = "Destination";
            var itemNode = (sender as MenuItem)?.DataContext as FileNodeViewModel;
            PerformDeleteForPanel("Destination", permanent: false, itemNode);
        }

        private void OnDestinationPermanentDeleteNodeClick(object? sender, RoutedEventArgs e)
        {
            _lastFocusedPanel = "Destination";
            var itemNode = (sender as MenuItem)?.DataContext as FileNodeViewModel;
            PerformDeleteForPanel("Destination", permanent: true, itemNode);
        }

        private List<FileNodeViewModel> GetSelectedNodesForPanel(string panel)
        {
            var list = new List<FileNodeViewModel>();
            var treeView = (panel == "Destination") ? DestinationGrid?.Children.OfType<TreeView>().FirstOrDefault() : SourceTreeView;
            if (treeView != null)
            {
                foreach (var item in treeView.SelectedItems)
                {
                    if (item is FileNodeViewModel fn && fn.Name != ".." && fn.Name != "(Empty Folder)")
                    {
                        list.Add(fn);
                    }
                }
            }
            return list;
        }

        private void PerformDelete(bool permanent)
        {
            string activePanel = _lastFocusedPanel;
            if (_vm.SelectedDestinationNodes.Count > 0 || _vm.SelectedDestinationNode != null)
            {
                if (_vm.SelectedSourceNodes.Count == 0 && _vm.SelectedSourceNode == null)
                {
                    activePanel = "Destination";
                }
            }
            PerformDeleteForPanel(activePanel, permanent);
        }

        private async void PerformDeleteForPanel(string panel, bool permanent, FileNodeViewModel? targetOverride = null)
        {
            _lastFocusedPanel = panel;
            List<FileNodeViewModel> targets;

            if (targetOverride != null)
            {
                targets = new List<FileNodeViewModel> { targetOverride };
            }
            else
            {
                targets = GetSelectedNodesForPanel(panel);
            }

            if (targets.Count == 0) return;

            string displayName = targets.Count == 1 ? targets[0].Name : $"{targets.Count} selected items";

            var settings = Services.AppSettings.Load();
            bool isSourcePanel = panel == "Source";
            if (permanent || isSourcePanel || settings.ConfirmOnDelete)
            {
                string promptTitle = isSourcePanel ? $"[Source Game File] {displayName}" : displayName;
                var dialog = new Views.ConfirmDeleteWindow(promptTitle, isPermanent: permanent || isSourcePanel);
                bool? confirmed = await dialog.ShowDialog<bool?>(this);
                if (confirmed != true) return;

                if (!permanent && !isSourcePanel && dialog.DontAskAgain)
                {
                    settings.ConfirmOnDelete = false;
                    settings.Save();
                }
            }

            if (panel == "Destination")
            {
                _vm.DeleteSelectedDestinationNode(targets, permanent);
            }
            else
            {
                await _vm.DeleteSelectedSourceNodeAsync(targets, permanent);
            }

            this.Activate();
            this.Focus();
        }

        // --- DOUBLE TAP EXPLORER ENTRY HANDLERS ---
        private async void OnSourceTreeDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            var node = _vm.SelectedSourceNode;
            if (node == null) return;

            if (node.Name == "..")
            {
                _vm.SourceNavigateUp();
                return;
            }

            if (node.IsDirectory || node.IsArchive)
            {
                node.IsExpanded = !node.IsExpanded;
            }
            else
            {
                string ext = Path.GetExtension(node.Name).ToLowerInvariant();
                if (ext == ".wav" || ext == ".snd")
                {
                    _vm.Preview.ToggleAudioPlay();
                }
            }
        }

        private async void OnTrackBadgeDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            e.Handled = true;
            await _vm.OpenConvertModalForTrackAsync();
        }

        private void OnDestinationTreeDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            var node = _vm.SelectedDestinationNode;
            if (node != null)
            {
                _vm.NavigateIntoDestinationNode(node);
            }
        }

        private void OnOpenNodeClick(object? sender, RoutedEventArgs e)
        {
            var node = GetTargetNode(sender);
            if (node == null) return;

            if (node.IsDirectory || node.IsArchive)
            {
                node.IsExpanded = !node.IsExpanded;
            }
            else if (!string.IsNullOrEmpty(node.AbsolutePath) && System.IO.File.Exists(node.AbsolutePath))
            {
                MainViewModel.OpenWithDefaultApp(node.AbsolutePath);
            }
            else if (!string.IsNullOrEmpty(node.VirtualPath))
            {
                // PAK-internal file: Extract to %TEMP%/tdr_tools and open with system default app
                try
                {
                    byte[]? data = _vm.ReadAllBytesForNode(node);
                    if (data != null && data.Length > 0)
                    {
                        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tdr_tools");
                        System.IO.Directory.CreateDirectory(tempDir);
                        string tempPath = System.IO.Path.Combine(tempDir, node.Name);
                        System.IO.File.WriteAllBytes(tempPath, data);
                        MainViewModel.OpenWithDefaultApp(tempPath);
                        _vm.LogSession($"Extracted PAK file to %TEMP% & launched editor: {node.Name}");
                    }
                }
                catch (Exception ex)
                {
                    _vm.LogSession($"[ERROR] Failed to open PAK file '{node.Name}': {ex.Message}");
                }
            }
        }

        private FileNodeViewModel? GetTargetNode(object? sender)
        {
            if (sender is MenuItem menuItem)
            {
                if (menuItem.DataContext is FileNodeViewModel node)
                    return node;

                if (menuItem.Parent is ContextMenu contextMenu)
                {
                    var target = contextMenu.PlacementTarget;
                    if (target is TreeViewItem treeItem && treeItem.DataContext is FileNodeViewModel itemNode)
                        return itemNode;

                    if (target is Control ctrl && ctrl.DataContext is FileNodeViewModel ctrlNode)
                        return ctrlNode;

                    if (target == DestinationTreeView || (target as Visual)?.GetVisualAncestors().Contains(DestinationGrid) == true)
                    {
                        return _vm.SelectedDestinationNode ?? _vm.SelectedDestinationNodes.FirstOrDefault();
                    }
                    if (target == SourceTreeView || (target as Visual)?.GetVisualAncestors().Contains(SourceTreeView) == true)
                    {
                        return _vm.SelectedSourceNode ?? _vm.SelectedSourceNodes.FirstOrDefault();
                    }
                }
            }

            if (_lastFocusedPanel == "Destination")
            {
                return _vm.SelectedDestinationNode ?? _vm.SelectedDestinationNodes.FirstOrDefault();
            }

            return _vm.SelectedSourceNode ?? _vm.SelectedSourceNodes.FirstOrDefault();
        }

        private async void OnCopyFileClick(object? sender, RoutedEventArgs e)
        {
            var node = GetTargetNode(sender);
            if (node == null)
            {
                _vm.LogSession("[WARN] No item selected for Copy File");
                return;
            }

            string? targetPath = node.AbsolutePath;
            if (string.IsNullOrEmpty(targetPath) && !string.IsNullOrEmpty(node.VirtualPath))
            {
                string basePath = !string.IsNullOrEmpty(_vm.SourceRootPath) ? _vm.SourceRootPath : System.IO.Directory.GetCurrentDirectory();
                targetPath = System.IO.Path.Combine(basePath, node.VirtualPath);
            }

            if (string.IsNullOrEmpty(targetPath))
            {
                targetPath = node.Name;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null && !string.IsNullOrEmpty(targetPath))
            {
                await topLevel.Clipboard.SetTextAsync(targetPath);
                _vm.LogSession($"Copied file path to OS clipboard: {targetPath}");
            }
        }

        private async void OnCopyPathClick(object? sender, RoutedEventArgs e)
        {
            var node = GetTargetNode(sender);
            if (node == null)
            {
                _vm.LogSession("[WARN] No item selected for Copy Path");
                return;
            }

            string path = !string.IsNullOrEmpty(node.AbsolutePath)
                ? node.AbsolutePath
                : (!string.IsNullOrEmpty(node.VirtualPath) ? node.VirtualPath : node.Name);

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null && !string.IsNullOrEmpty(path))
            {
                await topLevel.Clipboard.SetTextAsync(path);
                _vm.LogSession($"Copied path to clipboard: {path}");
            }
            else
            {
                _vm.LogSession($"[WARN] Clipboard unavailable or path empty for '{node.Name}'");
            }
        }

        private void OnOpenContainingFolderClick(object? sender, RoutedEventArgs e)
        {
            var node = GetTargetNode(sender);
            if (node == null) return;

            string? targetPath = node.AbsolutePath;
            if (string.IsNullOrEmpty(targetPath) && !string.IsNullOrEmpty(node.VirtualPath))
            {
                string basePath = !string.IsNullOrEmpty(_vm.SourceRootPath) ? _vm.SourceRootPath : System.IO.Directory.GetCurrentDirectory();
                targetPath = System.IO.Path.Combine(basePath, node.VirtualPath);
            }

            if (!string.IsNullOrEmpty(targetPath))
            {
                MainViewModel.OpenContainingFolderAndSelectFile(targetPath);
                _vm.LogSession($"Opened containing folder & highlighted item: {targetPath}");
            }
        }

        private void OnPropertiesClick(object? sender, RoutedEventArgs e)
        {
            var node = GetTargetNode(sender);
            if (node == null) return;

            var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

            if (node.IsArchive)
            {
                if (!string.IsNullOrEmpty(node.AbsolutePath) && System.IO.File.Exists(node.AbsolutePath))
                {
                    MainViewModel.OpenSystemProperties(node.AbsolutePath, hwnd);
                }
            }
            else if (!string.IsNullOrEmpty(node.VirtualPath) && !string.IsNullOrEmpty(node.AbsolutePath) &&
                     node.AbsolutePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(node.AbsolutePath))
            {
                MainViewModel.OpenSystemProperties(node.AbsolutePath, hwnd);
            }
            else if (!string.IsNullOrEmpty(node.AbsolutePath) && (System.IO.File.Exists(node.AbsolutePath) || System.IO.Directory.Exists(node.AbsolutePath)))
            {
                MainViewModel.OpenSystemProperties(node.AbsolutePath, hwnd);
            }
        }

        private void OnExtractMenuClick(object? sender, RoutedEventArgs e)
        {
            _vm.ExtractSelected();
        }

        private void OnQuickConvertTrackMenuClick(object? sender, RoutedEventArgs e)
        {
            _vm.QuickConvertSelectedTrack();
        }

        private async void OnConvertTrackMenuClick(object? sender, RoutedEventArgs e)
        {
            await _vm.OpenConvertModalForTrackAsync();
        }

        private async void OnRefreshSourceClick(object? sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_vm.SourceRootPath))
            {
                await _vm.IndexDirectory(_vm.SourceRootPath);
            }
        }

        private void OnRefreshDestinationClick(object? sender, RoutedEventArgs e)
        {
            _vm.RefreshDestinationTree();
            _vm.LogSession("Refreshed Destination tree");
        }

        private void OnCreateNewFolderInDestinationClick(object? sender, RoutedEventArgs e)
        {
            var parentNode = GetTargetNode(sender) ?? _vm.SelectedDestinationNode;
            _vm.CreateNewFolderInDestination(parentNode);
        }

        private async void OnIndexButtonClick(object? sender, RoutedEventArgs e)
        {
            await _vm.ReindexAllAsync();
        }

        private async void OnRebuildArchiveClick(object? sender, RoutedEventArgs e)
        {
            if (_vm.SelectedSourceNode != null)
            {
                await _vm.RebuildArchiveAsync(_vm.SelectedSourceNode);
            }
        }

        private void OnSourceViewTreeClick(object? sender, RoutedEventArgs e) => _vm.SourceViewMode = ViewModels.FileViewMode.Tree;
        private void OnSourceViewListClick(object? sender, RoutedEventArgs e) => _vm.SourceViewMode = ViewModels.FileViewMode.DetailsList;
        private void OnSourceViewGridClick(object? sender, RoutedEventArgs e) => _vm.SourceViewMode = ViewModels.FileViewMode.GridTiles;

        private void OnSortNameClick(object? sender, RoutedEventArgs e) => _vm.SortSourceFlatList("Name");
        private void OnSortSizeClick(object? sender, RoutedEventArgs e) => _vm.SortSourceFlatList("Size");
        private void OnSortTypeClick(object? sender, RoutedEventArgs e) => _vm.SortSourceFlatList("Type");
        private void OnSortSourceClick(object? sender, RoutedEventArgs e) => _vm.SortSourceFlatList("Source");

        private void OnClearLogClick(object? sender, RoutedEventArgs e)
        {
            Services.LogService.Instance.Clear();
            _vm.LogLines.Clear();
            _vm.LogSession("Session log cleared.");
        }

        private void OnLogFilterAllClick(object? sender, RoutedEventArgs e) => _vm.SetLogFilter("All");
        private void OnLogFilterGltfClick(object? sender, RoutedEventArgs e) => _vm.SetLogFilter("GLTF");
        private void OnLogFilterObjClick(object? sender, RoutedEventArgs e) => _vm.SetLogFilter("OBJ");
        private void OnLogFilterWarningsClick(object? sender, RoutedEventArgs e) => _vm.SetLogFilter("Warnings");
        private void OnLogFilterSummariesClick(object? sender, RoutedEventArgs e) => _vm.SetLogFilter("Summaries");

        private async void OnCopySelectedLogLinesClick(object? sender, RoutedEventArgs e)
        {
            if (LogListBox?.SelectedItems == null || LogListBox.SelectedItems.Count == 0) return;
            var lines = LogListBox.SelectedItems.OfType<Services.LogEntry>().Select(x => x.FormattedText);
            string text = string.Join(Environment.NewLine, lines);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }

        private async void OnCopyAllLogLinesClick(object? sender, RoutedEventArgs e)
        {
            string text = Services.LogService.Instance.GetAllText();
            if (string.IsNullOrEmpty(text) && _vm.LogLines.Count > 0)
            {
                text = string.Join(Environment.NewLine, _vm.LogLines.Select(x => x.FormattedText));
            }
            if (string.IsNullOrEmpty(text)) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }

        private async void OnCopyWarningsLogLinesClick(object? sender, RoutedEventArgs e)
        {
            string text = Services.LogService.Instance.GetFilteredText(e => MainViewModel.MatchesLogFilter(e, "Warnings"));
            if (string.IsNullOrWhiteSpace(text)) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }

        private async void OnCopyErrorsLogLinesClick(object? sender, RoutedEventArgs e)
        {
            string text = Services.LogService.Instance.GetFilteredText(e => e.Level == Services.LogLevel.Error ||
                                                                            e.Message.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                                                                            e.Message.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                                                                            e.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(text)) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }

        private async void OnCopySummariesLogLinesClick(object? sender, RoutedEventArgs e)
        {
            string text = Services.LogService.Instance.GetFilteredText(e => MainViewModel.MatchesLogFilter(e, "Summaries"));
            if (string.IsNullOrWhiteSpace(text)) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }

        private void OnLogListBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C)
            {
                OnCopySelectedLogLinesClick(sender, e);
            }
        }

        private void OnToggleAudioPlayClick(object? sender, RoutedEventArgs e)
        {
            _vm.Preview.ToggleAudioPlay();
        }

        private void OnToggleAudioLoopClick(object? sender, RoutedEventArgs e)
        {
            _vm.Preview.ToggleAudioLoop();
        }

        private void OnToggleAudioMuteClick(object? sender, RoutedEventArgs e)
        {
            _vm.Preview.ToggleAudioMute();
        }
    }
}