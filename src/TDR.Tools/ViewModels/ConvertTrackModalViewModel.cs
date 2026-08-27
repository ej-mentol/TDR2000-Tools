using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using TDR.PakLib;
using TDR.PakLib.Formats;
using TDR.Tools.Export;
using TDR.Tools.Services;

namespace TDR.Tools.ViewModels
{
    public enum AssetOrigin
    {
        LocalToTrack,
        InheritedFromBase,
        LocalToVariant,
        ExternalShared
    }

    public class HieNodeViewModel : INotifyPropertyChanged
    {
        private bool? _isSelected = true;
        private bool _isExpanded = false;
        private bool _isVisible = true;

        public string Name { get; set; } = string.Empty;
        public bool ShowTopSeparator { get; set; } = false;
        public string VirtualPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public bool IsBaseTrackAsset { get; set; }
        public AssetOrigin Origin { get; set; } = AssetOrigin.LocalToTrack;
        public string NodeType { get; set; } = "MeshFile";
        public ObservableCollection<HieNodeViewModel> Children { get; } = new();
        public HieNodeViewModel? Parent { get; set; }
        public Action? OnSelectionChangedCallback { get; set; }

        public bool? IsSelected
        {
            get => _isSelected;
            set
            {
                bool? targetValue = value;
                if (_isSelected != targetValue)
                {
                    _isSelected = targetValue;
                    OnPropertyChanged();

                    // Cascade check/uncheck to all children when toggled by user click
                    bool cascadeValue = targetValue ?? true;
                    foreach (var child in Children)
                    {
                        child.SetSelectedFromParent(cascadeValue);
                    }

                    // Refresh parent selection state
                    Parent?.UpdateParentSelectedState();
                    OnSelectionChangedCallback?.Invoke();
                }
            }
        }

        internal void SetSelectedFromParent(bool value)
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
                foreach (var child in Children)
                {
                    child.SetSelectedFromParent(value);
                }
            }
        }

        internal void UpdateParentSelectedState()
        {
            if (Children.Count == 0) return;
            int checkedCount = Children.Count(c => c.IsSelected == true);
            int uncheckedCount = Children.Count(c => c.IsSelected == false);

            bool? newState;
            if (checkedCount == Children.Count) newState = true;
            else if (uncheckedCount == Children.Count) newState = false;
            else newState = null; // Partial selection (indeterminate)

            if (_isSelected != newState)
            {
                _isSelected = newState;
                OnPropertyChanged(nameof(IsSelected));
                Parent?.UpdateParentSelectedState();
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(); }
        }

        public bool IsThreeState => IsDirectory || Children.Count > 0;

        public string BadgeText => NodeType == "TrackLayerRoot" ? "" : Origin switch
        {
            AssetOrigin.ExternalShared => "EXTERNAL",
            AssetOrigin.LocalToVariant => "VARIANT",
            AssetOrigin.InheritedFromBase => "BASE",
            _ => IsBaseTrackAsset ? "BASE" : ""
        };

        public bool HasBadge => !string.IsNullOrEmpty(BadgeText) && !IsDirectory;

        public string BadgeBackground => Origin switch
        {
            AssetOrigin.ExternalShared => "#4B3263",
            AssetOrigin.LocalToVariant => "#2E4F2F",
            _ => "#1E3A5F"
        };

        public string BadgeForeground => Origin switch
        {
            AssetOrigin.ExternalShared => "#C678DD",
            AssetOrigin.LocalToVariant => "#98C379",
            _ => "#61AFEF"
        };

        public string NodeForeground => IsDirectory
            ? (NodeType == "TrackLayerRoot" ? "#FFFFFF" : "#D1D5DB")
            : Origin switch
            {
                AssetOrigin.ExternalShared => "#C678DD",
                AssetOrigin.LocalToVariant => "#98C379",
                _ => "#61AFEF"
            };

        public string NodeFontWeight => NodeType == "TrackLayerRoot" ? "SemiBold" : "Normal";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ConvertTrackModalViewModel : INotifyPropertyChanged
    {
        public const string PresetAllSupported = "All supported resources";
        public const string PresetCustom = "Custom Selection";

        private bool _exportObj = true;
        private bool _exportGltf = true;
        private bool _exportArmatures = false;
        private bool _exportPngTextures = true;
        private bool _includeMovableProps = true;
        private bool _exportSceneJson = true;
        private bool _useZeroOriginForJsonAssets = true;
        private bool _useGrouping = true;
        private bool _useLocalCoords = false;
        private bool _verboseLog = false;
        private string _searchHieQuery = string.Empty;
        private string _selectedVariant = PresetAllSupported;
        private bool _isUpdatingFromPreset = false;

        public string TrackName { get; set; } = string.Empty;
        public string TrackTxtPath { get; set; } = string.Empty;
        public string ResolvedDescriptorPath { get; set; } = string.Empty;
        public string ResolvedDescriptorDisplay => string.IsNullOrEmpty(ResolvedDescriptorPath)
            ? "(will resolve at export time)"
            : ResolvedDescriptorPath;

        public string OutputDirectory { get; set; } = string.Empty;

        public List<string> AvailableVariants { get; set; } = new()
        {
            PresetAllSupported
        };

        public string SelectedVariant
        {
            get => _selectedVariant;
            set
            {
                if (_selectedVariant != value)
                {
                    _selectedVariant = value;
                    OnPropertyChanged();

                    if (!_isUpdatingFromPreset)
                    {
                        ApplyPresetToTree(value);
                    }
                }
            }
        }

        public bool ExportObj
        {
            get => _exportObj;
            set { _exportObj = value; OnPropertyChanged(); }
        }

        public bool ExportGltf
        {
            get => _exportGltf;
            set { _exportGltf = value; OnPropertyChanged(); }
        }

        public bool ExportArmatures
        {
            get => _exportArmatures;
            set { _exportArmatures = value; OnPropertyChanged(); }
        }

        public bool ExportPngTextures
        {
            get => _exportPngTextures;
            set { _exportPngTextures = value; OnPropertyChanged(); }
        }

        public bool IncludeMovableProps
        {
            get => _includeMovableProps;
            set { _includeMovableProps = value; OnPropertyChanged(); }
        }

        public bool ExportSceneJson
        {
            get => _exportSceneJson;
            set { _exportSceneJson = value; OnPropertyChanged(); }
        }

        public bool UseZeroOriginForJsonAssets
        {
            get => _useZeroOriginForJsonAssets;
            set { _useZeroOriginForJsonAssets = value; OnPropertyChanged(); }
        }

        public bool UseGrouping
        {
            get => _useGrouping;
            set { _useGrouping = value; OnPropertyChanged(); }
        }

        public bool UseLocalCoords
        {
            get => _useLocalCoords;
            set { _useLocalCoords = value; OnPropertyChanged(); }
        }

        public bool VerboseLog
        {
            get => _verboseLog;
            set { _verboseLog = value; OnPropertyChanged(); }
        }

        public string SearchHieQuery
        {
            get => _searchHieQuery;
            set
            {
                if (_searchHieQuery != value)
                {
                    _searchHieQuery = value;
                    OnPropertyChanged();
                    FilterHieTree();
                }
            }
        }

        public ObservableCollection<HieNodeViewModel> HieTreeNodes { get; } = new();

        public Action? RequestClose { get; set; }
        public Action<ConvertTrackModalViewModel>? RequestStartExport { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public ConvertTrackModalViewModel()
        {
            var settings = Services.AppSettings.Load();
            LoadFromSettings(settings);
        }

        public void LoadFromSettings(AppSettings settings)
        {
            ExportObj = settings.ExportObj;
            ExportGltf = settings.ExportGltf;
            ExportArmatures = settings.ExportArmatures;
            ExportPngTextures = settings.ExportPngTextures;
            IncludeMovableProps = settings.IncludeMovableProps;
            ExportSceneJson = settings.ExportSceneJson;
            UseZeroOriginForJsonAssets = settings.UseZeroOriginForJsonAssets;
            UseGrouping = settings.UseGrouping;
            UseLocalCoords = settings.UseLocalCoords;
            VerboseLog = settings.VerboseLog;
        }

        public void InitializeTree(PakManager vfs, string? sourceRootPath, string initialPreset)
        {
            Services.TrackTreeBuilder.PopulateModalTree(vfs, this, sourceRootPath);
            SelectedVariant = initialPreset;
            ApplyPresetToTree(initialPreset);
        }

        public void NotifyUserTreeToggled()
        {
            if (_isUpdatingFromPreset) return;
            if (!AvailableVariants.Contains(PresetCustom))
            {
                AvailableVariants.Add(PresetCustom);
                OnPropertyChanged(nameof(AvailableVariants));
            }
            _selectedVariant = PresetCustom;
            OnPropertyChanged(nameof(SelectedVariant));
        }

        public void ApplyPresetToTree(string preset)
        {
            if (preset == PresetCustom || string.IsNullOrEmpty(preset)) return;

            _isUpdatingFromPreset = true;
            try
            {
                string pLower = preset.ToLowerInvariant().Trim();

                foreach (var parentNode in HieTreeNodes)
                {
                    bool selectLayer = false;
                    string layerKey = parentNode.VirtualPath.ToLowerInvariant();
                    string layerName = parentNode.Name.ToLowerInvariant();

                    if (pLower == PresetAllSupported.ToLowerInvariant())
                    {
                        selectLayer = true;
                    }
                    else if (pLower.StartsWith("base track only", StringComparison.OrdinalIgnoreCase))
                    {
                        selectLayer = parentNode.IsBaseTrackAsset ||
                                     (!layerKey.Contains("race") && !layerKey.Contains("mission") && !layerKey.Contains("multi"));
                    }
                    else if (pLower.StartsWith("all races", StringComparison.OrdinalIgnoreCase))
                    {
                        selectLayer = layerKey.Contains("race");
                    }
                    else if (pLower.StartsWith("all missions", StringComparison.OrdinalIgnoreCase))
                    {
                        selectLayer = layerKey.Contains("mission");
                    }
                    else if (pLower.StartsWith("geometry only", StringComparison.OrdinalIgnoreCase))
                    {
                        selectLayer = parentNode.IsBaseTrackAsset;
                    }
                    else
                    {
                        // Specific variant selected (e.g. "Hollowood_Race1" or "Race1")
                        string cleanTarget = pLower.Replace("_", "").Replace(" ", "");
                        string cleanKey = layerKey.Replace("_", "").Replace(" ", "");
                        string cleanName = layerName.Replace("_", "").Replace(" ", "");

                        bool isMatch = cleanKey.Contains(cleanTarget) || cleanTarget.Contains(cleanKey) ||
                                       cleanName.Contains(cleanTarget) || cleanTarget.Contains(cleanName);

                        selectLayer = isMatch;
                    }

                    // Set full branch selection (clean [x] checkmark or [ ] uncheck, no stray squares)
                    parentNode.SetSelectedFromParent(selectLayer);
                    parentNode.IsExpanded = false; // Collapsed by default for clean, compact tree
                }
            }
            finally
            {
                _isUpdatingFromPreset = false;
            }
        }

        public void SetAllHieNodesSelected(IEnumerable<HieNodeViewModel> nodes, bool selected)
        {
            foreach (var node in nodes)
            {
                node.IsSelected = selected;
                if (node.Children.Count > 0)
                {
                    SetAllHieNodesSelected(node.Children, selected);
                }
            }
        }

        public void SetAllHieNodesExpanded(IEnumerable<HieNodeViewModel> nodes, bool expanded)
        {
            foreach (var node in nodes)
            {
                node.IsExpanded = expanded;
                if (node.Children.Count > 0)
                {
                    SetAllHieNodesExpanded(node.Children, expanded);
                }
            }
        }

        public List<string> GetSelectedHiePaths()
        {
            var result = new List<string>();
            CollectSelectedHiePaths(HieTreeNodes, result);
            return result;
        }

        public static void CollectSelectedHiePaths(IEnumerable<HieNodeViewModel> nodes, List<string> result)
        {
            foreach (var node in nodes)
            {
                if (!node.IsDirectory && node.IsSelected == true && !string.IsNullOrEmpty(node.VirtualPath))
                {
                    result.Add(node.VirtualPath);
                }
                if (node.Children.Count > 0)
                {
                    CollectSelectedHiePaths(node.Children, result);
                }
            }
        }

        public void FilterHieTree()
        {
            string query = SearchHieQuery.Trim().ToLowerInvariant();
            foreach (var node in HieTreeNodes)
            {
                FilterHieNode(node, query);
            }
        }

        private bool FilterHieNode(HieNodeViewModel node, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                node.IsVisible = true;
                foreach (var child in node.Children) FilterHieNode(child, query);
                return true;
            }

            bool selfMatch = node.Name.ToLowerInvariant().Contains(query) || node.VirtualPath.ToLowerInvariant().Contains(query);
            bool childMatch = false;

            foreach (var child in node.Children)
            {
                if (FilterHieNode(child, query)) childMatch = true;
            }

            node.IsVisible = selfMatch || childMatch;
            if (childMatch) node.IsExpanded = true;
            return node.IsVisible;
        }

        public void Cancel()
        {
            RequestClose?.Invoke();
        }

        public void ConfirmExport()
        {
            var settings = Services.AppSettings.Load();
            settings.ExportObj = ExportObj;
            settings.ExportGltf = ExportGltf;
            settings.ExportArmatures = ExportArmatures;
            settings.ExportPngTextures = ExportPngTextures;
            settings.IncludeMovableProps = IncludeMovableProps;
            settings.ExportSceneJson = ExportSceneJson;
            settings.UseZeroOriginForJsonAssets = UseZeroOriginForJsonAssets;
            settings.UseGrouping = UseGrouping;
            settings.UseLocalCoords = UseLocalCoords;
            settings.VerboseLog = VerboseLog;
            settings.Save();

            RequestStartExport?.Invoke(this);
            RequestClose?.Invoke();
        }
    }
}
