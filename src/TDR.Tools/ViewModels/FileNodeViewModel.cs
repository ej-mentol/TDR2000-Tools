using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TDR.Tools.ViewModels
{
    public enum FileNodeType
    {
        Directory,
        Archive,
        TrackDescriptor,
        Geometry,
        LooseFile,
        Unknown
    }

    public class FileNodeViewModel : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _virtualPath = string.Empty;
        private string _absolutePath = string.Empty;
        private FileNodeType _nodeType = FileNodeType.Unknown;
        private bool _isDirectory;
        private bool _isArchive;
        private bool _isTrack;
        private string _badgeText = string.Empty;
        private string _metaText = string.Empty;
        private string _icon = "📄";
        private long _size;
        private bool _isExpanded;
        private bool _isVirtual;
        private bool _isEditing;
        private string _editName = string.Empty;
        private string _sourceArchiveName = string.Empty;

        public bool IsVirtual
        {
            get => _isVirtual;
            set
            {
                if (SetField(ref _isVirtual, value))
                {
                    if (_isVirtual && string.IsNullOrEmpty(BadgeText))
                    {
                        BadgeText = "DRAFT";
                    }
                    UpdateIcon();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string SourceArchiveName
        {
            get => _sourceArchiveName;
            set => SetField(ref _sourceArchiveName, value);
        }

        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (SetField(ref _isEditing, value) && value)
                {
                    EditName = Name;
                }
            }
        }

        public string EditName
        {
            get => _editName;
            set => SetField(ref _editName, value);
        }

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        public string VirtualPath
        {
            get => _virtualPath;
            set => SetField(ref _virtualPath, value);
        }

        public string AbsolutePath
        {
            get => _absolutePath;
            set => SetField(ref _absolutePath, value);
        }

        public FileNodeType NodeType
        {
            get => _nodeType;
            set => SetField(ref _nodeType, value);
        }

        public bool IsDirectory
        {
            get => _isDirectory;
            set => SetField(ref _isDirectory, value);
        }

        public bool IsArchive
        {
            get => _isArchive;
            set => SetField(ref _isArchive, value);
        }

        public bool IsTrack
        {
            get => _isTrack;
            set => SetField(ref _isTrack, value);
        }

        public string BadgeText
        {
            get => _badgeText;
            set
            {
                if (SetField(ref _badgeText, value))
                {
                    OnPropertyChanged(nameof(BadgeBrush));
                    OnPropertyChanged(nameof(BadgeSubtleBrush));
                }
            }
        }

        public IBrush? BadgeBrush
        {
            get
            {
                string key = BadgeText switch
                {
                    "Mission" => "BadgeMissionBrush",
                    "Race" => "BadgeRaceBrush",
                    _ => "BadgeTrackBrush"
                };
                if (Application.Current != null && Application.Current.TryGetResource(key, null, out object? res) && res is IBrush brush)
                {
                    return brush;
                }
                return null;
            }
        }

        public IBrush? BadgeSubtleBrush
        {
            get
            {
                string key = BadgeText switch
                {
                    "Mission" => "BadgeMissionSubtleBrush",
                    "Race" => "BadgeRaceSubtleBrush",
                    _ => "BadgeTrackSubtleBrush"
                };
                if (Application.Current != null && Application.Current.TryGetResource(key, null, out object? res) && res is IBrush brush)
                {
                    return brush;
                }
                return null;
            }
        }

        public string MetaText
        {
            get => _metaText;
            set => SetField(ref _metaText, value);
        }

        public string FormattedSize
        {
            get
            {
                if (IsDirectory) return string.Empty;
                if (Size >= 0)
                {
                    if (Size >= 1024 * 1024 * 1024) return $"{Size / (1024.0 * 1024.0 * 1024.0):F1} GB";
                    if (Size >= 1024 * 1024) return $"{Size / (1024.0 * 1024.0):F1} MB";
                    if (Size >= 1024) return $"{Size / 1024.0:F1} KB";
                    return $"{Size} B";
                }
                if (!string.IsNullOrEmpty(MetaText)) return MetaText;
                if (IsArchive) return "PAK";
                return string.Empty;
            }
        }

        public string Icon
        {
            get => _icon;
            set => SetField(ref _icon, value);
        }

        public long Size
        {
            get => _size;
            set => SetField(ref _size, value);
        }

        private Action<FileNodeViewModel>? _onExpandCallback;

        public Action<FileNodeViewModel>? OnExpandCallback
        {
            get => _onExpandCallback;
            set => _onExpandCallback = value;
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetField(ref _isExpanded, value) && value)
                {
                    var callback = _onExpandCallback;
                    _onExpandCallback = null;
                    callback?.Invoke(this);
                }
            }
        }

        public ObservableCollection<FileNodeViewModel> Children { get; } = new();

        public FileNodeViewModel? Parent { get; set; }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private string _iconKey = "IconFile";
        private string _iconBrushKey = "TextSecondaryBrush";

        public string IconKey
        {
            get => _iconKey;
            set
            {
                if (SetField(ref _iconKey, value))
                {
                    OnPropertyChanged(nameof(IconData));
                }
            }
        }

        public string IconBrushKey
        {
            get => _iconBrushKey;
            set
            {
                if (SetField(ref _iconBrushKey, value))
                {
                    OnPropertyChanged(nameof(IconBrush));
                }
            }
        }

        public Avalonia.Media.Geometry? IconData
        {
            get
            {
                if (Avalonia.Application.Current != null &&
                    Avalonia.Application.Current.TryGetResource(_iconKey, null, out object? res) &&
                    res is Avalonia.Media.Geometry geom)
                {
                    return geom;
                }
                return null;
            }
        }

        public Avalonia.Media.IBrush? IconBrush
        {
            get
            {
                if (Avalonia.Application.Current != null &&
                    Avalonia.Application.Current.TryGetResource(_iconBrushKey, null, out object? res) &&
                    res is Avalonia.Media.IBrush brush)
                {
                    return brush;
                }
                return null;
            }
        }

        public void UpdateIcon()
        {
            if (Name == "..")
            {
                IconKey = "IconParentDir";
                IconBrushKey = "AccentBrush";
                return;
            }

            if (IsTrack)
            {
                if (IsArchive)
                {
                    IconKey = "IconArchive";
                    IconBrushKey = "ArchiveColorBrush";
                }
                else if (IsDirectory)
                {
                    IconKey = "IconFolder";
                    IconBrushKey = "AccentBrush";
                }
                else
                {
                    IconKey = "IconFile";
                    IconBrushKey = "TextSecondaryBrush";
                }

                if (string.IsNullOrEmpty(BadgeText))
                {
                    BadgeText = "Track";
                }
            }
            else if (IsArchive)
            {
                IconKey = "IconArchive";
                IconBrushKey = "ArchiveColorBrush";
                MetaText = "archive";
            }
            else if (IsDirectory)
            {
                IconKey = "IconFolder";
                IconBrushKey = "AccentBrush";
            }
            else if (NodeType == FileNodeType.Geometry)
            {
                IconKey = "IconGeometry";
                IconBrushKey = "TextSecondaryBrush";
                MetaText = "geometry";
            }
            else
            {
                IconKey = "IconFile";
                IconBrushKey = "TextSecondaryBrush";
            }
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
