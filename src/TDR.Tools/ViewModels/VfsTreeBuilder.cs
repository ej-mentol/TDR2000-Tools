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
            bool showDirFiles = false)
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
                        long pakSize = (!string.IsNullOrEmpty(f.ArchivePath) && File.Exists(f.ArchivePath)) ? new FileInfo(f.ArchivePath).Length : f.Size;
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

            // 2. Validate Track Badges on Archive and Folder Nodes
            foreach (var node in nodeCache.Values)
            {
                if (node.IsArchive || node.IsDirectory)
                {
                    string baseName = Path.GetFileNameWithoutExtension(node.Name).ToLowerInvariant();
                    string virtPath = (node.VirtualPath ?? "").Replace('\\', '/').ToLowerInvariant();

                    bool isSystemAsset = baseName is "animation" or "powerups" or "cars" or "carma" or "sound" or "sfx" or "fonts" or "pip" or "hud" or "frontend" or "system" or "menu" or "attributes" or "stuff" or "drones" or "pathfollowers" or "strings" or "sky sphere" or "animated props" or "level convsoft" or "level radar" or "level breakable" or "level props" or "level shadows"
                                      || virtPath.StartsWith("cars/") || virtPath.StartsWith("powerups/") || virtPath.StartsWith("animations/") || virtPath.StartsWith("sound/") || virtPath.StartsWith("frontend/") || virtPath.StartsWith("menu/") || virtPath.StartsWith("attributes/") || virtPath.StartsWith("stuff/") || virtPath.StartsWith("drones/") || virtPath.StartsWith("pathfollowers/") || virtPath.StartsWith("strings/")
                                      || virtPath.Contains("/sky sphere/") || virtPath.Contains("/animated props/") || virtPath.Contains("/level convsoft/") || virtPath.Contains("/level radar/") || virtPath.Contains("/level breakable/") || virtPath.Contains("/level props/") || virtPath.Contains("/level shadows/");

                    if (!isSystemAsset)
                    {
                        bool isTrackNode = (node.IsDirectory || node.IsArchive) &&
                                           TrackDiscovery.IsWeakTrackCandidate($"{node.VirtualPath}/{baseName}.txt") &&
                                           (isTrackValidator == null || isTrackValidator($"{node.VirtualPath}/{baseName}.txt"));

                        if (!isTrackNode && node.IsArchive)
                        {
                            string normArchivePath = (node.AbsolutePath ?? node.VirtualPath ?? "").Replace('\\', '/');
                            string archiveFileName = Path.GetFileName(normArchivePath);

                            isTrackNode = vfs.GetFiles().Any(f =>
                                !string.IsNullOrEmpty(f.ArchivePath) &&
                                (f.ArchivePath.Replace('\\', '/').Equals(normArchivePath, StringComparison.OrdinalIgnoreCase) ||
                                 Path.GetFileName(f.ArchivePath).Equals(archiveFileName, StringComparison.OrdinalIgnoreCase)) &&
                                TrackDiscovery.IsWeakTrackCandidate(f.Name) &&
                                (isTrackValidator == null || isTrackValidator(f.Name)));

                            if (!isTrackNode && !string.IsNullOrEmpty(archiveFileName))
                            {
                                string lowerArchive = archiveFileName.ToLowerInvariant();
                                if (!lowerArchive.Equals("carma.pak", StringComparison.OrdinalIgnoreCase) && !lowerArchive.Equals("system.pak", StringComparison.OrdinalIgnoreCase))
                                {
                                    isTrackNode = lowerArchive.Contains("race") || lowerArchive.Contains("mission") || lowerArchive.Contains("track");
                                }
                            }
                        }

                        if (!isTrackNode && node.IsDirectory)
                        {
                            // Badge escalation guard: only the immediate track folder (tracks/<name>)
                            // gets a badge — never its parents ("tracks/", "assets/", root).
                            // A valid track folder has VirtualPath of the form "tracks/<trackname>"
                            // (exactly one slash, first segment is "tracks").
                            string vp = (node.VirtualPath ?? "").Replace('\\', '/').ToLowerInvariant().TrimEnd('/');
                            var vpParts = vp.Split('/', StringSplitOptions.RemoveEmptyEntries);
                            bool isDirectTrackFolder = vpParts.Length == 2 &&
                                                       vpParts[0].Equals("tracks", StringComparison.OrdinalIgnoreCase);

                            if (isDirectTrackFolder)
                            {
                                // Check that at least one file inside this folder is a confirmed track descriptor
                                string prefix = node.VirtualPath!;
                                isTrackNode = vfs.GetFiles().Any(f =>
                                    !string.IsNullOrEmpty(f.Name) &&
                                    f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                    TrackDiscovery.IsWeakTrackCandidate(f.Name) &&
                                    (isTrackValidator == null || isTrackValidator(f.Name)));
                            }
                        }

                        if (isTrackNode)
                        {
                            node.IsTrack = true;
                            node.BadgeText = node.Name.Contains("mission", StringComparison.OrdinalIgnoreCase) ? "Mission" :
                                             node.Name.Contains("race", StringComparison.OrdinalIgnoreCase) ? "Race" : "Track";
                            node.UpdateIcon();
                        }
                    }
                }
            }

            // 3. Filter by search query if provided
            bool queryActive = !string.IsNullOrWhiteSpace(searchQuery);

            // Sort root level: 1. Folders (A-Z) -> 2. Archives (A-Z) -> 3. Files (A-Z)
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

            // 2. Enumerate physical disk directories to ensure complete browser view (prevents locking inside subfolders)
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
                            nodeCache[dirName] = dirNode;

                            if (!queryActive || MatchesSearchQuery(dirNode, searchQuery))
                            {
                                targetNodes.Add(dirNode);
                            }
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
                            var fileNode = new FileNodeViewModel
                            {
                                Name = fileName,
                                VirtualPath = fileName,
                                AbsolutePath = file,
                                IsArchive = isPak,
                                IsDirectory = false,
                                NodeType = isPak ? FileNodeType.Archive : FileNodeType.LooseFile,
                                Size = fileInfo.Length
                            };
                            fileNode.UpdateIcon();
                            nodeCache[fileName] = fileNode;

                            if (!queryActive || MatchesSearchQuery(fileNode, searchQuery))
                            {
                                targetNodes.Add(fileNode);
                            }
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
                    var fileInfo = new FileInfo(file);
                    if (!showDirFiles && fileInfo.Name.EndsWith(".dir", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!queryActive || fileInfo.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        var fileNode = new FileNodeViewModel
                        {
                            Name = fileInfo.Name,
                            AbsolutePath = file,
                            Size = fileInfo.Length
                        };
                        fileNode.UpdateIcon();
                        targetNodes.Add(fileNode);
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
                    var fileInfo = new FileInfo(file);
                    if (!showDirFiles && fileInfo.Name.EndsWith(".dir", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!queryActive || fileInfo.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        var fileNode = new FileNodeViewModel
                        {
                            Name = fileInfo.Name,
                            AbsolutePath = file,
                            Size = fileInfo.Length,
                            Parent = parentNode
                        };
                        fileNode.UpdateIcon();
                        parentNode.Children.Add(fileNode);
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
                    if (lowerDir.Contains("race") || lowerDir.Contains("mission"))
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

                if (queryActive && !cleanName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) && !f.ArchivePath.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                    continue;

                string arcName = !string.IsNullOrEmpty(f.ArchivePath) ? Path.GetFileName(f.ArchivePath) : "Loose";
                var node = CreateNode(fileName, cleanName, f.ArchivePath, f.IsLooseFile, f.Size, isTrackValidator ?? (_ => false));
                node.SourceArchiveName = arcName;
                targetNodes.Add(node);
            }
        }
    }
}
