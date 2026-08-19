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

        private bool _isAudioFile;
        private bool _isAudioPlaying;
        private bool _isAudioLooping;
        private bool _isAudioMuted;
        private double _audioProgressPercent;
        private string _audioFormatText = string.Empty;
        private string _audioDurationText = "0:00";
        private byte[]? _currentAudioBytes;

        private double _currentAudioTotalSeconds;

        public event PropertyChangedEventHandler? PropertyChanged;

        public PreviewViewModel(Func<FileNodeViewModel, byte[]?> readFileBytes, Action<string> log)
        {
            _readFileBytes = readFileBytes ?? throw new ArgumentNullException(nameof(readFileBytes));
            _log = log ?? throw new ArgumentNullException(nameof(log));

            Services.AudioPlayerService.Instance.PlaybackStateChanged += (isPlaying) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    IsAudioPlaying = isPlaying;
                });
            };

            Services.AudioPlayerService.Instance.ProgressUpdated += (elapsed, total, percent) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    AudioProgressPercent = percent;
                    if (total > 0) _currentAudioTotalSeconds = total;
                    int curSec = (int)Math.Floor(elapsed);
                    int totSec = (int)Math.Round(_currentAudioTotalSeconds);
                    AudioDurationText = $"{curSec / 60}:{curSec % 60:D2} / {totSec / 60}:{totSec % 60:D2}";
                });
            };
        }

        // ──────────────────────────────────────────────
        //  Bindable Properties
        // ──────────────────────────────────────────────

        public double AudioProgressPercent
        {
            get => _audioProgressPercent;
            set => SetField(ref _audioProgressPercent, value);
        }

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

        public bool IsAudioFile
        {
            get => _isAudioFile;
            set => SetField(ref _isAudioFile, value);
        }

        public bool IsAudioPlaying
        {
            get => _isAudioPlaying;
            set
            {
                if (SetField(ref _isAudioPlaying, value))
                {
                    OnPropertyChanged(nameof(AudioPlayIconData));
                }
            }
        }

        public string AudioFormatText
        {
            get => _audioFormatText;
            set => SetField(ref _audioFormatText, value);
        }

        public string AudioDurationText
        {
            get => _audioDurationText;
            set => SetField(ref _audioDurationText, value);
        }

        public string AudioPlayIconData => IsAudioPlaying
            ? "M6 19h4V5H6v14zm8-14v14h4V5h-4z"
            : "M8 5v14l11-7z";

        public bool IsAudioLooping
        {
            get => _isAudioLooping;
            set
            {
                if (SetField(ref _isAudioLooping, value))
                {
                    Services.AudioPlayerService.Instance.IsLooping = value;
                    OnPropertyChanged(nameof(AudioLoopBrush));
                }
            }
        }

        public bool IsAudioMuted
        {
            get => _isAudioMuted;
            set
            {
                if (SetField(ref _isAudioMuted, value))
                {
                    OnPropertyChanged(nameof(AudioMuteIconData));
                }
            }
        }

        public string AudioLoopBrush => IsAudioLooping ? "#38BDF8" : "#888888";
        public string AudioMuteBrush => IsAudioMuted ? "#EF4444" : "#888888";

        public string AudioMuteIconData => IsAudioMuted
            ? "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73 4.27 3zM12 4L9.91 6.09 12 8.18V4z"
            : "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02z";

        public void ToggleAudioLoop()
        {
            IsAudioLooping = !IsAudioLooping;
        }

        public void ToggleAudioMute()
        {
            Services.AudioPlayerService.Instance.ToggleMute(_currentAudioBytes);
            IsAudioMuted = Services.AudioPlayerService.Instance.IsMuted;
        }

        public void ToggleAudioPlay()
        {
            if (_currentAudioBytes == null || _currentAudioBytes.Length == 0) return;
            Services.AudioPlayerService.Instance.TogglePlay(_currentAudioBytes);
            IsAudioPlaying = Services.AudioPlayerService.Instance.IsPlaying;
            if (IsAudioPlaying)
            {
                _log($"Playing '{PreviewTitle}' ({AudioFormatText})");
            }
            else
            {
                _log($"Stopped playing '{PreviewTitle}'");
            }
        }

        public void StopAudio()
        {
            Services.AudioPlayerService.Instance.Stop();
            IsAudioPlaying = false;
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
            StopAudio();
            IsAudioFile = false;
            _currentAudioBytes = null;
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

            if (node.IsDirectory || node.IsArchive)
            {
                ClosePreview();
                PreviewTitle = node.Name;
                if (node.IsArchive)
                {
                    PreviewSubTitle = $"PAK Archive · {node.Children.Count} items · {node.FormattedSize}";
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Type", "TDR2000 Archive Container (.pak/.dir)"));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Inner Files", node.Children.Count.ToString()));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Size on Disk", node.FormattedSize));

                    // Calculate Fragmentation if .dir exists
                    if (!string.IsNullOrEmpty(node.AbsolutePath) && File.Exists(node.AbsolutePath))
                    {
                        string dirPath = Path.ChangeExtension(node.AbsolutePath, ".dir");
                        if (File.Exists(dirPath))
                        {
                            try
                            {
                                var entries = TDR.PakLib.TDRArchive.ParseTrieIndex(dirPath);
                                long activePayload = 0;
                                for (int i = 0; i < entries.Count; i++)
                                    activePayload += entries[i].Size;

                                long pakSize = new FileInfo(node.AbsolutePath).Length;
                                long deadSpace = Math.Max(0, pakSize - activePayload);
                                double fragRatio = pakSize > 0 ? ((double)deadSpace / pakSize) * 100.0 : 0;

                                string payloadStr = activePayload < 1024 * 1024
                                    ? $"{activePayload / 1024.0:F1} KB"
                                    : $"{activePayload / (1024.0 * 1024.0):F1} MB";

                                PreviewMetadata.Add(new KeyValuePair<string, string>("Active Payload", payloadStr));

                                if (fragRatio < 1.0 || deadSpace < 8192)
                                {
                                    PreviewMetadata.Add(new KeyValuePair<string, string>("Fragmentation", "0% (Clean)"));
                                }
                                else
                                {
                                    string deadStr = deadSpace < 1024 * 1024
                                        ? $"{deadSpace / 1024.0:F1} KB"
                                        : $"{deadSpace / (1024.0 * 1024.0):F1} MB";
                                    PreviewMetadata.Add(new KeyValuePair<string, string>("Fragmentation", $"{fragRatio:F1}% ({deadStr} wasted)"));
                                    if (fragRatio >= 5.0)
                                    {
                                        PreviewMetadata.Add(new KeyValuePair<string, string>("Status", "Rebuild / Defrag recommended"));
                                    }
                                }
                            }
                            catch
                            {
                                // Fail gracefully if index is unreadable
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(node.BadgeText))
                    {
                        PreviewMetadata.Add(new KeyValuePair<string, string>("Descriptor", node.BadgeText));
                    }
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Path", !string.IsNullOrEmpty(node.AbsolutePath) ? node.AbsolutePath : node.VirtualPath));
                }
                else
                {
                    PreviewSubTitle = $"Directory · {node.Children.Count} items";
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Type", "Folder / Directory"));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Items Count", node.Children.Count.ToString()));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Path", !string.IsNullOrEmpty(node.AbsolutePath) ? node.AbsolutePath : node.VirtualPath));
                }
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
                IsAudioFile = false;
                StopAudio();

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
                else if (ext == ".wav" || ext == ".snd")
                {
                    IsAudioFile = true;
                    _currentAudioBytes = fileBytes;
                    var wavInfo = Services.AudioPlayerService.Instance.ParseWavHeader(fileBytes);
                    AudioFormatText = wavInfo.FormatText;
                    _currentAudioTotalSeconds = wavInfo.DurationSeconds;
                    int totalSec = (int)Math.Round(wavInfo.DurationSeconds);
                    string totalDurationStr = $"{totalSec / 60}:{totalSec % 60:D2}";
                    AudioDurationText = $"0:00 / {totalDurationStr}";

                    PreviewMetadata.Add(new KeyValuePair<string, string>("Format", $"Audio ({ext})"));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Audio Info", AudioFormatText));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Duration", totalDurationStr));
                    PreviewMetadata.Add(new KeyValuePair<string, string>("Source", node.IsArchive ? "VFS PAK Memory" : "Disk"));
                    _log($"Loaded audio: '{node.Name}' ({AudioFormatText}, {totalDurationStr})");
                }
                else if (ext == ".txt" || ext == ".h" || ext == ".ini" || ext == ".cfg" || ext == ".json" || ext == ".xml" || ext == ".descriptor" || ext == ".mat" || ext == ".hed")
                {
                    string textContent = DecodeTextAuto(fileBytes);
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
                        string textContent = DecodeTextAuto(fileBytes);
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
                    string textContent = DecodeTextAuto(fileBytes);
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

        public static string DecodeTextAuto(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;

            // 1. Check for UTF-8 / UTF-16 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            // 2. Strict UTF-8 verification
            try
            {
                var utf8Strict = new System.Text.UTF8Encoding(false, true);
                return utf8Strict.GetString(bytes);
            }
            catch
            {
                // 3. Fallback for legacy game text files (Windows-1251 / Windows-1252 / Latin1)
                try
                {
                    var enc = System.Text.Encoding.GetEncoding("windows-1251");
                    return enc.GetString(bytes);
                }
                catch
                {
                    return System.Text.Encoding.Latin1.GetString(bytes);
                }
            }
        }

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
