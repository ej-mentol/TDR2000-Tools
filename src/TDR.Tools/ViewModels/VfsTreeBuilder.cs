using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using TDR.PakLib;
using TDR.PakLib.Formats;

namespace TDR.Tools.ViewModels
{
    public static class VfsTreeBuilder
    {
        public static void BuildSourceTree(
            PakManager vfs,
            string rootPath,
            ObservableCollection<FileNodeViewModel> targetNodes,
            Func<string, bool> isTrackValidator,
            string searchQuery = "",
            bool showDirFiles = false,
            Action<string>? logSession = null)
        {
            targetNodes.Clear();

            // 1. Add Parent Row (..) if parent directory exists
            var parentDir = Directory.GetParent(rootPath);
            if (parentDir != null)
            {
                var parentNode = new FileNodeViewModel
                {
                    Name = "..",
                    VirtualPath = "..",
                    AbsolutePath = parentDir.FullName,
                    IsDirectory = true,
                    Icon = "←",
                    MetaText = "parent"
                };
                parentNode.UpdateIcon();
                targetNodes.Add(parentNode);
            }

            var nodeCache = new Dictionary<string, FileNodeViewModel>(StringComparer.OrdinalIgnoreCase);
            var rootNodesList = new List<FileNodeViewModel>();

            string canonicalRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var files = vfs.GetFiles();
            foreach (var f in files)
            {
                if (string.IsNullOrEmpty(f.ArchivePath)) continue;

                // Scope Filter: Calculate relative path from canonicalRoot to the file's ArchivePath on disk
                string relFromRoot = Path.GetRelativePath(canonicalRoot, f.ArchivePath).Replace('\\', '/');
                if (relFromRoot.StartsWith("..") || Path.IsPathRooted(relFromRoot))
                {
                    // Skip files/archives outside the current rootPath view
                    continue;
                }

                var parts = new List<string>();

                if (!f.IsLooseFile)
                {
                    // File belongs to a .pak archive — build relative path: [pakRelativePath...] -> [internalFileName]
                    var pakParts = relFromRoot.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    parts.AddRange(pakParts);
                    parts.Add(Path.GetFileName(f.Name));
                }
                else
                {
                    // Loose file on disk within rootPath
                    var looseParts = relFromRoot.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    parts.AddRange(looseParts);
                }

                FileNodeViewModel? parent = null;
                string accumulatedPath = "";

                for (int i = 0; i < parts.Count; i++)
                {
                    string part = parts[i];
                    accumulatedPath = i == 0 ? part : $"{accumulatedPath}/{part}";
                    bool isLeaf = (i == parts.Count - 1);

                    if (nodeCache.TryGetValue(accumulatedPath, out var existingNode))
                    {
                        parent = existingNode;
                        continue;
                    }

                    FileNodeViewModel newNode;
                    bool isPak = part.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || part.EndsWith(".dir", StringComparison.OrdinalIgnoreCase);

                    if (isPak)
                    {
                        long pakSize = File.Exists(f.ArchivePath) ? new FileInfo(f.ArchivePath).Length : f.Size;
                        newNode = new FileNodeViewModel
                        {
                            Name = part,
                            VirtualPath = accumulatedPath,
                            AbsolutePath = f.ArchivePath,
                            IsArchive = true,
                            NodeType = FileNodeType.Archive,
                            Size = pakSize
                        };
                        newNode.UpdateIcon();
                    }
                    else if (isLeaf)
                    {
                        newNode = CreateNode(part, accumulatedPath, f.ArchivePath, f.IsLooseFile, f.Size, isTrackValidator);
                    }
                    else
                    {
                        string dirAbsPath = Path.GetFullPath(Path.Combine(canonicalRoot, accumulatedPath));
                        newNode = new FileNodeViewModel
                        {
                            Name = part,
                            VirtualPath = accumulatedPath,
                            AbsolutePath = dirAbsPath,
                            IsDirectory = true,
                            NodeType = FileNodeType.Directory
                        };
                        newNode.UpdateIcon();
                    }

                    newNode.Parent = parent;
                    nodeCache[accumulatedPath] = newNode;

                    if (parent == null)
                    {
                        rootNodesList.Add(newNode);
                    }
                    else
                    {
                        parent.Children.Add(newNode);
                    }

                    parent = newNode;
                }
            }

            // 2. Validate Track Badges on Archive and Folder Nodes via CARMA.pak/races.txt
            var confirmedTrackFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folderBadgeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            byte[]? racesBytes = vfs.LoadFileContext("races.txt", "CARMA") ?? vfs.LoadFile("races.txt");
            if (racesBytes != null && racesBytes.Length > 0)
            {
                var racesFile = RacesFile.Parse(racesBytes);
                foreach (var race in racesFile.Races)
                {
                    if (string.IsNullOrEmpty(race.Track)) continue;

                    string badge;
                    if (race.Type == 64 || race.Track.Contains("multiplayer", StringComparison.OrdinalIgnoreCase) || race.Track.Contains("_mp", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "MP";
                    }
                    else if (race.IsMission || race.Type == 32 || race.Track.Contains("mission", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "Mission";
                    }
                    else if (race.Type == 31 || race.Track.Contains("race", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "Race";
                    }
                    else
                    {
                        badge = "Track";
                    }

                    confirmedTrackFolders.Add($"tracks/{race.Track}");
                    confirmedTrackFolders.Add(race.Track);
                    folderBadgeTypes[$"tracks/{race.Track}"] = badge;
                    folderBadgeTypes[race.Track] = badge;

                    string baseTrack = TrackDiscovery.GetBaseTrackName(race.Track);
                    if (!string.IsNullOrEmpty(baseTrack))
                    {
                        confirmedTrackFolders.Add($"tracks/{baseTrack}");
                        confirmedTrackFolders.Add(baseTrack);
                        if (!folderBadgeTypes.ContainsKey(baseTrack))
                        {
                            folderBadgeTypes[$"tracks/{baseTrack}"] = "Track";
                            folderBadgeTypes[baseTrack] = "Track";
                        }
                    }
                }
            }

            foreach (var f in vfs.GetFiles())
            {
                if (string.IsNullOrEmpty(f.Name)) continue;
                if (!TrackDiscovery.IsWeakTrackCandidate(f.Name)) continue;
                if (isTrackValidator != null && !isTrackValidator(f.Name)) continue;

                string norm = f.Name.Replace('\\', '/');

                var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Equals("tracks", StringComparison.OrdinalIgnoreCase))
                {
                    string trackVariantFolder = parts[1];
                    if (!folderBadgeTypes.ContainsKey(trackVariantFolder))
                    {
                        string trackVariantBadge = trackVariantFolder.Contains("mission", StringComparison.OrdinalIgnoreCase) ? "Mission" :
                                                   trackVariantFolder.Contains("race", StringComparison.OrdinalIgnoreCase) ? "Race" :
                                                   (trackVariantFolder.Contains("multiplayer", StringComparison.OrdinalIgnoreCase) || trackVariantFolder.Contains("_mp", StringComparison.OrdinalIgnoreCase)) ? "MP" : "Track";

                        confirmedTrackFolders.Add($"tracks/{trackVariantFolder}");
                        confirmedTrackFolders.Add(trackVariantFolder);
                        folderBadgeTypes[$"tracks/{trackVariantFolder}"] = trackVariantBadge;
                        folderBadgeTypes[trackVariantFolder] = trackVariantBadge;
                    }
                }
                else
                {
                    string baseTrack = TrackDiscovery.GetBaseTrackName(Path.GetFileNameWithoutExtension(f.Name));
                    if (!string.IsNullOrEmpty(baseTrack) && !folderBadgeTypes.ContainsKey(baseTrack))
                    {
                        string badge = f.Name.Contains("mission", StringComparison.OrdinalIgnoreCase) ? "Mission" :
                                       f.Name.Contains("race", StringComparison.OrdinalIgnoreCase) ? "Race" :
                                       (f.Name.Contains("multiplayer", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("_mp", StringComparison.OrdinalIgnoreCase)) ? "MP" : "Track";

                        confirmedTrackFolders.Add(baseTrack);
                        folderBadgeTypes[baseTrack] = badge;
                    }
                }
            }

            foreach (var node in nodeCache.Values)
            {
                if (node.IsArchive || node.IsDirectory)
                {
                    string baseName = Path.GetFileNameWithoutExtension(node.Name).ToLowerInvariant();
                    string virtPath = (node.VirtualPath ?? "").Replace('\\', '/').Trim('/');

                    string systemReason = "";
                    if (baseName is "animation" or "powerups" or "cars" or "carma" or "sound" or "sfx" or "fonts" or "pip" or "hud" or "frontend" or "system" or "menu" or "attributes" or "stuff" or "drones" or "pathfollowers" or "strings" or "sky sphere" or "animated props" or "level convsoft" or "level radar" or "level breakable" or "level props" or "level shadows" or "level drones" or "drone paths" or "level sound" or "level lights" or "tracks" or "assets" or "root")
                        systemReason = $"SystemBaseName('{baseName}')";
                    else if (virtPath.StartsWith("cars/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('cars/')";
                    else if (virtPath.StartsWith("powerups/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('powerups/')";
                    else if (virtPath.StartsWith("animations/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('animations/')";
                    else if (virtPath.StartsWith("animation/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('animation/')";
                    else if (virtPath.StartsWith("sound/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('sound/')";
                    else if (virtPath.StartsWith("frontend/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('frontend/')";
                    else if (virtPath.StartsWith("menu/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('menu/')";
                    else if (virtPath.StartsWith("attributes/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('attributes/')";
                    else if (virtPath.StartsWith("stuff/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('stuff/')";
                    else if (virtPath.StartsWith("drones/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('drones/')";
                    else if (virtPath.StartsWith("pathfollowers/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('pathfollowers/')";
                    else if (virtPath.StartsWith("strings/", StringComparison.OrdinalIgnoreCase)) systemReason = "Prefix('strings/')";
                    else if (virtPath.Contains("/sky sphere/", StringComparison.OrdinalIgnoreCase)) systemReason = "Contains('/sky sphere/')";
                    else if (virtPath.Contains("/animated props/", StringComparison.OrdinalIgnoreCase)) systemReason = "Contains('/animated props/')";
                    else if (virtPath.Contains("/level convsoft/", StringComparison.OrdinalIgnoreCase)) systemReason = "Contains('/level convsoft/')";
                    else if (virtPath.Contains("/level radar/", StringComparison.OrdinalIgnoreCase)) systemReason = "Contains('/level radar/')";
                    else if (virtPath.Contains("/level breakable/", StringComparison.OrdinalIgnoreCase)) systemReason = "Contains('/level breakable/')";
                    else if (virtPath.Contains("/level props/", StringComparison.OrdinalIgnoreCase)) systemReason = "Contains('/level props/')";
                    else if (virtPath.Contains("/level shadows/", StringComparison.OrdinalIgnoreCase)) systemReason = "Contains('/level shadows/')";
                    else if (virtPath.Contains("/level drones/", StringComparison.OrdinalIgnoreCase)) systemReason = "Contains('/level drones/')";
                    else if (virtPath.Contains("/drone paths/", StringComparison.OrdinalIgnoreCase)) systemReason = "Contains('/drone paths/')";

                    bool isSystemAsset = !string.IsNullOrEmpty(systemReason);

                    if (!isSystemAsset)
                    {
                        bool isTrackNode = false;
                        string badge = "Track";
                        string trackMatchRule = "";

                        if (folderBadgeTypes.TryGetValue(virtPath, out var b1))
                        {
                            isTrackNode = true;
                            badge = b1;
                            trackMatchRule = $"folderBadgeTypes['{virtPath}']";
                        }
                        else if (folderBadgeTypes.TryGetValue(node.Name, out var b2))
                        {
                            isTrackNode = true;
                            badge = b2;
                            trackMatchRule = $"folderBadgeTypes['{node.Name}']";
                        }
                        else if (confirmedTrackFolders.Contains(virtPath) || confirmedTrackFolders.Contains(node.Name))
                        {
                            isTrackNode = true;
                            badge = node.Name.Contains("mission", StringComparison.OrdinalIgnoreCase) ? "Mission" :
                                    node.Name.Contains("race", StringComparison.OrdinalIgnoreCase) ? "Race" : "Track";
                            trackMatchRule = "confirmedTrackFolders";
                        }
                        else if (TrackDiscovery.IsWeakTrackCandidate($"{virtPath}/{baseName}.txt") && (isTrackValidator == null || isTrackValidator($"{virtPath}/{baseName}.txt")))
                        {
                            isTrackNode = true;
                            badge = node.Name.Contains("mission", StringComparison.OrdinalIgnoreCase) ? "Mission" :
                                    node.Name.Contains("race", StringComparison.OrdinalIgnoreCase) ? "Race" : "Track";
                            trackMatchRule = $"WeakCandidate('{virtPath}/{baseName}.txt')";
                        }
                        else if (node.IsArchive && TrackDiscovery.IsWeakTrackCandidate(node.Name) && (isTrackValidator == null || isTrackValidator(node.Name)))
                        {
                            isTrackNode = true;
                            badge = node.Name.Contains("mission", StringComparison.OrdinalIgnoreCase) ? "Mission" :
                                    node.Name.Contains("race", StringComparison.OrdinalIgnoreCase) ? "Race" : "Track";
                            trackMatchRule = $"WeakCandidateArchive('{node.Name}')";
                        }

                        if (isTrackNode)
                        {
                            node.IsTrack = true;
                            node.BadgeText = badge;
                            node.UpdateIcon();
                        }
                    }
                }
            }

            // 2. Enumerate physical disk directories and files to ensure complete browser view (prevents locking inside subfolders)
            if (Directory.Exists(canonicalRoot))
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(canonicalRoot))
                    {
                        string dirName = Path.GetFileName(dir);
                        if (!nodeCache.ContainsKey(dirName))
                        {
                            var dirNode = new FileNodeViewModel
                            {
                                Name = dirName,
                                VirtualPath = dirName,
                                AbsolutePath = dir,
                                IsDirectory = true,
                                NodeType = FileNodeType.Directory
                            };
                            dirNode.UpdateIcon();
                            SetupLazyFolderExpansion(dirNode, searchQuery, logSession, showDirFiles);
                            nodeCache[dirName] = dirNode;
                            rootNodesList.Add(dirNode);
                        }
                    }

                    foreach (string file in Directory.GetFiles(canonicalRoot))
                    {
                        string fileName = Path.GetFileName(file);
                        if (!showDirFiles && fileName.EndsWith(".dir", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!nodeCache.ContainsKey(fileName))
                        {
                            var fileInfo = new FileInfo(file);
                            bool isPak = fileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".dir", StringComparison.OrdinalIgnoreCase);
                            long fileLen = 0;
                            try { fileLen = fileInfo.Length; } catch { }

                            var fileNode = new FileNodeViewModel
                            {
                                Name = fileName,
                                VirtualPath = fileName,
                                AbsolutePath = file,
                                IsArchive = isPak,
                                IsDirectory = false,
                                NodeType = isPak ? FileNodeType.Archive : FileNodeType.LooseFile,
                                Size = fileLen
                            };
                            fileNode.UpdateIcon();
                            nodeCache[fileName] = fileNode;
                            rootNodesList.Add(fileNode);
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Access restricted to protected folder — normal OS behavior
                }
                catch (Exception)
                {
                    // Ignore non-critical disk scan exception
                }
            }

            // 3. Filter by search query if provided and sort root level: 1. Folders (A-Z) -> 2. Files and Archives (A-Z)
            bool queryActive = !string.IsNullOrWhiteSpace(searchQuery);

            var sortedRoot = rootNodesList
                .OrderBy(n => GetSortPriority(n))
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var node in sortedRoot)
            {
                SortChildren(node);
                if (!queryActive || MatchesSearchQuery(node, searchQuery))
                {
                    targetNodes.Add(node);
                }
            }
        }

        private static int GetSortPriority(FileNodeViewModel node)
        {
            if (node.IsDirectory) return 0; // 1. Folders FIRST
            return 1;                       // 2. All Files (including .pak archives) SECOND (sorted A-Z)
        }

        public static void BuildDestinationTree(string rootPath, ObservableCollection<FileNodeViewModel> targetNodes, string searchQuery = "", Action<string>? logSession = null, bool showDirFiles = false)
        {
            targetNodes.Clear();
            if (string.IsNullOrEmpty(rootPath)) return;

            try
            {
                if (!Directory.Exists(rootPath))
                {
                    Directory.CreateDirectory(rootPath);
                }

                // 1. Add Parent Row (..) if parent directory exists
                var parentDir = Directory.GetParent(rootPath);
                if (parentDir != null)
                {
                    var parentNode = new FileNodeViewModel
                    {
                        Name = "..",
                        VirtualPath = "..",
                        AbsolutePath = parentDir.FullName,
                        IsDirectory = true,
                        Icon = "←",
                        MetaText = "parent"
                    };
                    parentNode.UpdateIcon();
                    targetNodes.Add(parentNode);
                }

                bool queryActive = !string.IsNullOrWhiteSpace(searchQuery);

                // 2. Add Folders FIRST (sorted A-Z) with 1-level lazy expansion
                IEnumerable<string> dirs = Array.Empty<string>();
                try
                {
                    dirs = Directory.GetDirectories(rootPath).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
                }
                catch (UnauthorizedAccessException)
                {
                    // Access restricted to protected folder — normal system behavior
                }
                catch (Exception ex)
                {
                    logSession?.Invoke($"[ERROR] BuildDestinationTree: Failed reading directories in '{rootPath}': {ex.Message}");
                }

                foreach (string dir in dirs)
                {
                    string dirName = Path.GetFileName(dir);
                    if (!queryActive || dirName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        var dirNode = new FileNodeViewModel
                        {
                            Name = dirName,
                            AbsolutePath = dir,
                            IsDirectory = true
                        };
                        CheckAndApplyDiskTrackBadge(dirNode, dir);
                        dirNode.UpdateIcon();

                        SetupLazyFolderExpansion(dirNode, searchQuery, logSession, showDirFiles);
                        targetNodes.Add(dirNode);
                    }
                }

                // 3. Add Files SECOND (sorted A-Z)
                IEnumerable<string> files = Array.Empty<string>();
                try
                {
                    files = Directory.GetFiles(rootPath).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
                }
                catch (UnauthorizedAccessException)
                {
                    // Access restricted to protected folder — normal system behavior
                }
                catch (Exception ex)
                {
                    logSession?.Invoke($"[ERROR] BuildDestinationTree: Failed reading files in '{rootPath}': {ex.Message}");
                }

                foreach (string file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (!fileInfo.Exists) continue;
                        if (!showDirFiles && fileInfo.Name.EndsWith(".dir", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!queryActive || fileInfo.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                        {
                            long len = 0;
                            try { len = fileInfo.Length; } catch { }

                            var fileNode = new FileNodeViewModel
                            {
                                Name = fileInfo.Name,
                                AbsolutePath = file,
                                Size = len
                            };
                            fileNode.UpdateIcon();
                            targetNodes.Add(fileNode);
                        }
                    }
                    catch
                    {
                        // Skip individual inaccessible or transient files
                    }
                }
            }
            catch (Exception ex)
            {
                logSession?.Invoke($"[ERROR] BuildDestinationTree failed for '{rootPath}': {ex.Message}");
            }
        }

        private static void SetupLazyFolderExpansion(FileNodeViewModel folderNode, string searchQuery, Action<string>? logSession, bool showDirFiles = false)
        {
            if (string.IsNullOrEmpty(folderNode.AbsolutePath) || !Directory.Exists(folderNode.AbsolutePath)) return;

            try
            {
                bool hasChildren = Directory.EnumerateFileSystemEntries(folderNode.AbsolutePath).Any();
                if (hasChildren)
                {
                    folderNode.Children.Add(new FileNodeViewModel { Name = "...", VirtualPath = "__dummy__" });
                    folderNode.OnExpandCallback = (node) =>
                    {
                        PopulateDiskDirectoryChildren(node.AbsolutePath, node, searchQuery, logSession, showDirFiles);
                    };
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Protected system directory (AppData / .cache) — no expand arrow
            }
            catch (Exception ex)
            {
                logSession?.Invoke($"[WARN] Lazy expansion check failed for '{folderNode.Name}': {ex.Message}");
            }
        }

        private static void PopulateDiskDirectoryChildren(string dirPath, FileNodeViewModel parentNode, string searchQuery, Action<string>? logSession = null, bool showDirFiles = false)
        {
            parentNode.Children.Clear();
            if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return;

            bool queryActive = !string.IsNullOrWhiteSpace(searchQuery);

            try
            {
                foreach (string subDir in Directory.GetDirectories(dirPath).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                {
                    string subDirName = Path.GetFileName(subDir);
                    if (!queryActive || subDirName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        var subDirNode = new FileNodeViewModel
                        {
                            Name = subDirName,
                            AbsolutePath = subDir,
                            IsDirectory = true,
                            Parent = parentNode
                        };
                        CheckAndApplyDiskTrackBadge(subDirNode, subDir);
                        subDirNode.UpdateIcon();

                        SetupLazyFolderExpansion(subDirNode, searchQuery, logSession, showDirFiles);
                        parentNode.Children.Add(subDirNode);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Protected folder — silent
            }
            catch (Exception ex)
            {
                logSession?.Invoke($"[ERROR] Failed reading subdirectories of '{dirPath}': {ex.Message}");
            }

            try
            {
                foreach (string file in Directory.GetFiles(dirPath).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (!fileInfo.Exists) continue;
                        if (!showDirFiles && fileInfo.Name.EndsWith(".dir", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!queryActive || fileInfo.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                        {
                            long len = 0;
                            try { len = fileInfo.Length; } catch { }

                            var fileNode = new FileNodeViewModel
                            {
                                Name = fileInfo.Name,
                                AbsolutePath = file,
                                Size = len,
                                Parent = parentNode
                            };
                            fileNode.UpdateIcon();
                            parentNode.Children.Add(fileNode);
                        }
                    }
                    catch
                    {
                        // Skip individual inaccessible or transient files
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Protected folder — silent
            }
            catch (Exception ex)
            {
                logSession?.Invoke($"[ERROR] Failed reading files of '{dirPath}': {ex.Message}");
            }
        }

        private static void CheckAndApplyDiskTrackBadge(FileNodeViewModel node, string dirPath)
        {
            try
            {
                if (!Directory.Exists(dirPath)) return;
                string dirName = node.Name;
                string lower = dirName.ToLowerInvariant();
                if (lower is "tracks" or "assets" or "bin" or "obj" or "src" or "temp" or "export") return;

                string[] txtFiles = Directory.GetFiles(dirPath, "*.txt");
                bool isTrack = txtFiles.Any(f => TrackDiscovery.IsWeakTrackCandidate(Path.GetFileName(f)));
                if (isTrack)
                {
                    node.IsTrack = true;
                    node.NodeType = FileNodeType.TrackDescriptor;
                    node.BadgeText = dirName.Contains("mission", StringComparison.OrdinalIgnoreCase) ? "Mission" :
                                     dirName.Contains("race", StringComparison.OrdinalIgnoreCase) ? "Race" : "Track";
                }
            }
            catch { }
        }

        private static bool MatchesSearchQuery(FileNodeViewModel node, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            if (node.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var child in node.Children)
            {
                if (MatchesSearchQuery(child, query)) return true;
            }
            return false;
        }

        private static void SortChildren(FileNodeViewModel parent)
        {
            if (parent.Children.Count == 0) return;

            var sorted = parent.Children
                .OrderBy(c => GetSortPriority(c))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            parent.Children.Clear();
            foreach (var item in sorted)
            {
                SortChildren(item);
                parent.Children.Add(item);
            }
        }

        private static FileNodeViewModel CreateNode(
            string name,
            string virtualPath,
            string archivePath,
            bool isLoose,
            long size,
            Func<string, bool> isTrackValidator)
        {
            long resolvedSize = size;
            if (resolvedSize <= 0 && isLoose && !string.IsNullOrEmpty(archivePath) && File.Exists(archivePath))
            {
                try { resolvedSize = new FileInfo(archivePath).Length; } catch { }
            }

            var node = new FileNodeViewModel
            {
                Name = name,
                VirtualPath = virtualPath,
                AbsolutePath = archivePath,
                Size = resolvedSize,
                IsArchive = virtualPath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || virtualPath.EndsWith(".dir", StringComparison.OrdinalIgnoreCase)
            };

            if (node.IsDirectory || (!node.Name.Contains('.') && !string.IsNullOrEmpty(archivePath) && Directory.Exists(archivePath)))
            {
                node.IsDirectory = true;
                string lowerDir = name.ToLowerInvariant();
                if (!lowerDir.Equals("tracks") && !lowerDir.Equals("assets") && !lowerDir.Equals("bin") && !lowerDir.Equals("obj") && !lowerDir.Equals("src"))
                {
                    if ((isTrackValidator != null && isTrackValidator(virtualPath)) || lowerDir.Contains("race") || lowerDir.Contains("mission"))
                    {
                        node.IsTrack = true;
                        node.NodeType = FileNodeType.TrackDescriptor;
                        node.BadgeText = name.Contains("mission", StringComparison.OrdinalIgnoreCase) ? "Mission" :
                                         name.Contains("race", StringComparison.OrdinalIgnoreCase) ? "Race" : "Track";
                    }
                }
            }
            else if (name.EndsWith(".hie", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".msh", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mshs", StringComparison.OrdinalIgnoreCase))
            {
                node.NodeType = FileNodeType.Geometry;
            }
            else if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                node.NodeType = FileNodeType.TrackDescriptor;
            }

            node.UpdateIcon();
            return node;
        }

        public static void BuildFlatList(PakManager vfs, ObservableCollection<FileNodeViewModel> targetNodes, string searchQuery = "", Func<string, bool>? isTrackValidator = null)
        {
            targetNodes.Clear();
            if (vfs == null) return;

            var allFiles = vfs.GetFiles();
            bool queryActive = !string.IsNullOrWhiteSpace(searchQuery);

            foreach (var f in allFiles)
            {
                string cleanName = f.Name;
                string fileName = Path.GetFileName(cleanName);
                if (string.IsNullOrEmpty(fileName)) continue;

                string arcName = !string.IsNullOrEmpty(f.ArchivePath) ? Path.GetFileName(f.ArchivePath) : "Loose";
                if (queryActive && !cleanName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) && !arcName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                    continue;

                var node = CreateNode(fileName, cleanName, f.ArchivePath, f.IsLooseFile, f.Size, isTrackValidator ?? (_ => false));
                node.SourceArchiveName = arcName;
                targetNodes.Add(node);
            }
        }
    }
}
