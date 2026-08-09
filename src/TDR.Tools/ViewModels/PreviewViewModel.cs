using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using TDR.PakLib.Formats;
using TDR.Tools.Export;

namespace TDR.Tools.ViewModels
{
    /// <summary>
    /// Manages the Inspector / Preview drawer state and content.
    /// Extracted from MainViewModel to isolate preview rendering from navigation and export logic.
    /// 
    /// Dependencies are injected as delegates so this class has no direct reference to MainViewModel:
    ///   - readFileBytes: reads raw bytes for a FileNodeViewModel (from VFS or disk)
    ///   - log: writes a line to the session log
    /// </summary>
    public class PreviewViewModel : INotifyPropertyChanged
    {
        private readonly Func<FileNodeViewModel, byte[]?> _readFileBytes;
        private readonly Action<string> _log;

        private Bitmap? _previewImage;
        private string _previewTitle = string.Empty;
        private string _previewSubTitle = string.Empty;
        private bool _isPreviewVisible;
        private bool _isPreviewDrawerExpanded;
        private string? _previewText;

        public event PropertyChangedEventHandler? PropertyChanged;

        public PreviewViewModel(Func<FileNodeViewModel, byte[]?> readFileBytes, Action<string> log)
        {
            _readFileBytes = readFileBytes ?? throw new ArgumentNullException(nameof(readFileBytes));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        // ──────────────────────────────────────────────
        //  Bindable Properties
        // ──────────────────────────────────────────────

        public Bitmap? PreviewImage
        {
            get => _previewImage;
            set => SetField(ref _previewImage, value);
        }

        public string PreviewTitle
        {
            get => _previewTitle;
            set => SetField(ref _previewTitle, value);
        }

        public string PreviewSubTitle
        {
            get => _previewSubTitle;
            set => SetField(ref _previewSubTitle, value);
        }

        public bool IsPreviewVisible
        {
            get => _isPreviewVisible;
            set
            {
                if (SetField(ref _isPreviewVisible, value))
                {
                    OnPropertyChanged(nameof(IsPreviewDrawerOpen));
                }
            }
        }

        public bool IsPreviewDrawerExpanded
        {
            get => _isPreviewDrawerExpanded;
            set
            {
                if (SetField(ref _isPreviewDrawerExpanded, value))
                {
                    OnPropertyChanged(nameof(IsPreviewDrawerOpen));
                    OnPropertyChanged(nameof(PreviewDrawerModeBrush));
                }
            }
        }

        public string PreviewDrawerModeBrush => IsPreviewDrawerExpanded ? "#1F2D3A" : "Transparent";

        public bool IsPreviewDrawerOpen => IsPreviewDrawerExpanded;

        public string? PreviewText
        {
            get => _previewText;
            set
            {
                if (SetField(ref _previewText, value))
                {
                    OnPropertyChanged(nameof(IsPreviewTextVisible));
                }
            }
        }

        public bool IsPreviewTextVisible => !string.IsNullOrEmpty(PreviewText);

        public ObservableCollection<KeyValuePair<string, string>> PreviewMetadata { get; } = new();

        // ──────────────────────────────────────────────
        //  Public API
        // ──────────────────────────────────────────────

        public void ClosePreview()
        {
            PreviewImage = null;
            PreviewText = null;
            PreviewMetadata.Clear();
        }

        public void UpdatePreviewNode(FileNodeViewModel? node)
        {
            if (node == null)
            {
                ClosePreview();
                return;
            }

            if (node.IsDirectory)
            {
                ClosePreview();
                PreviewTitle = node.Name;
                PreviewSubTitle = $"Directory · {node.Children.Count} items";
                PreviewMetadata.Add(new KeyValuePair<string, string>("Type", "Folder / Directory"));
                PreviewMetadata.Add(new KeyValuePair<string, string>("Items Count", node.Children.Count.ToString()));
                PreviewMetadata.Add(new KeyValuePair<string, string>("Path", !string.IsNullOrEmpty(node.AbsolutePath) ? node.AbsolutePath : node.VirtualPath));
                return;
            }

            try
            {
                byte[]? fileBytes = _readFileBytes(node);

                if (fileBytes == null || fileBytes.Length == 0)
                {
                    ClosePreview();
                    PreviewTitle = node.Name;
                    PreviewSubTitle = "0 KB · Empty file";
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Status", "Empty or Unreadable"));
                    return;
                }

                string ext = Path.GetExtension(node.Name).ToLowerInvariant();
                PreviewTitle = node.Name;
                PreviewSubTitle = $"{fileBytes.Length / 1024.0:F1} KB · {node.MetaText}";
                PreviewMetadata.Clear();
                PreviewImage = null;
                PreviewText = null;

                if (ext == ".tga")
                {
                    PreviewImage = TgaDecoder.DecodeTga(fileBytes);
                    if (PreviewImage != null)
                    {
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Format", "Targa Image (.tga)"));
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Dimensions", $"{PreviewImage.PixelSize.Width} × {PreviewImage.PixelSize.Height}"));
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Source", node.IsArchive ? "VFS PAK Memory" : "Disk"));
                        _log($"In-memory preview decoded TGA: {node.Name} ({PreviewImage.PixelSize.Width}x{PreviewImage.PixelSize.Height})");
                    }
                }
                else if (ext == ".png" || ext == ".jpg" || ext == ".bmp")
                {
                    using var ms = new MemoryStream(fileBytes);
                    PreviewImage = new Bitmap(ms);
                    if (PreviewImage != null)
                    {
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Format", $"Image ({ext})"));
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Dimensions", $"{PreviewImage.PixelSize.Width} × {PreviewImage.PixelSize.Height}"));
                        _log($"In-memory preview image: {node.Name}");
                    }
                }
                else if (ext == ".txt" || ext == ".h" || ext == ".ini" || ext == ".cfg" || ext == ".json" || ext == ".xml" || ext == ".descriptor")
                {
                    string textContent = System.Text.Encoding.UTF8.GetString(fileBytes).TrimStart('\uFEFF');
                    if (textContent.Length > 4000)
                    {
                        textContent = textContent.Substring(0, 4000) + "\n... (truncated)";
                    }
                    PreviewText = textContent;
                    string[] lines = textContent.Split('\n');
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Format", $"Text File ({ext})"));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Total Lines", lines.Length.ToString()));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Source", node.IsArchive ? "VFS PAK Memory" : "Disk"));
                }
                else if (ext == ".hie")
                {
                    try
                    {
                        var hie = TDRHierarchy.Load(fileBytes, node.Name);
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Type", "Hierarchy (.hie)"));
                        PreviewMetadata.Add(new KeyValuePair<string, string>("HIE Version", hie.Version.ToString()));
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Anim FPS", $"{hie.AnimationFps:F0}"));
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Nodes Count", hie.Nodes.Count.ToString()));
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Mesh Nodes", hie.Nodes.Count(n => n.Type == TDRNode.NodeType.Mesh).ToString()));
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Unique Meshes", hie.Meshes.Count.ToString()));
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Textures", hie.Textures.Count.ToString()));
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Materials", hie.Materials.Count.ToString()));
                        if (hie.Meshes.Count > 0)
                        {
                            PreviewMetadata.Add(new KeyValuePair<string, string>("Primary Mesh", hie.Meshes[0]));
                        }
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Source", node.IsArchive ? "VFS PAK Memory (zIG)" : "Disk"));
                        _log($"In-memory preview parsed HIE: {node.Name} ({hie.Nodes.Count} nodes, {hie.Meshes.Count} meshes)");
                    }
                    catch
                    {
                        string textContent = System.Text.Encoding.UTF8.GetString(fileBytes).TrimStart('\uFEFF');
                        if (textContent.Length > 4000)
                        {
                            textContent = textContent.Substring(0, 4000) + "\n... (truncated)";
                        }
                        PreviewText = textContent;
                        string[] lines = textContent.Split('\n');
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Type", "Hierarchy Descriptor (.hie)"));
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Total Lines", lines.Length.ToString()));
                    }
                }
                else if (IsPrintableText(fileBytes))
                {
                    string textContent = System.Text.Encoding.UTF8.GetString(fileBytes).TrimStart('\uFEFF');
                    if (textContent.Length > 4000)
                    {
                        textContent = textContent.Substring(0, 4000) + "\n... (truncated)";
                    }
                    PreviewText = textContent;
                    string[] lines = textContent.Split('\n');
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Format", $"Text Descriptor ({ext})"));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Total Lines", lines.Length.ToString()));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Source", node.IsArchive ? "VFS PAK Memory (zIG)" : "Disk"));
                    _log($"In-memory text viewer loaded '{node.Name}' ({lines.Length} lines)");
                }
                else
                {
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Type", $"Binary File ({ext})"));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Uncompressed Size", $"{fileBytes.Length} bytes"));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Source", node.IsArchive ? "VFS PAK Memory (zIG)" : "Disk"));
                }
            }
            catch (Exception ex)
            {
                _log($"[ERROR] Preview failed for '{node.Name}': {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────
        //  Internal Helpers
        // ──────────────────────────────────────────────

        private static bool IsPrintableText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;
            int checkLength = Math.Min(bytes.Length, 512);
            int printableCount = 0;
            for (int i = 0; i < checkLength; i++)
            {
                byte b = bytes[i];
                if (b == 9 || b == 10 || b == 13 || (b >= 32 && b <= 126) || b >= 160)
                {
                    printableCount++;
                }
                else if (b == 0)
                {
                    return false;
                }
            }
            return (double)printableCount / checkLength > 0.85;
        }

        // ──────────────────────────────────────────────
        //  INotifyPropertyChanged
        // ──────────────────────────────────────────────

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
    }
}
