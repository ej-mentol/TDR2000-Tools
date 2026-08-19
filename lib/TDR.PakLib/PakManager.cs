using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TDR.PakLib
{
    /// <summary>
    /// Virtual File System manager for indexed TDR2000 PAK/DIR archives and disk files.
    /// Supports track-context prioritizing to prevent cross-track bare name leaks.
    /// </summary>
    public sealed class PakManager
    {
        public sealed class IndexedFile
        {
            public string Name { get; set; } = string.Empty;
            public string ArchivePath { get; set; } = string.Empty;
            public uint Offset { get; set; }
            public uint Size { get; set; }
            public bool IsLooseFile { get; set; }
        }

        private readonly Dictionary<string, IndexedFile> _fullPathIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<IndexedFile>> _fileNameIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<IndexedFile> _allFiles = new();

        public void IndexDirectory(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return;

            var dirFiles = Directory.GetFiles(rootPath, "*.dir", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (string dirPath in dirFiles)
            {
                string pakPath = Path.ChangeExtension(dirPath, ".pak");
                if (!File.Exists(pakPath)) continue;

                var entries = TDRArchive.ParseTrieIndex(dirPath);
                foreach (var entry in entries)
                {
                    string cleanName = TDRArchive.SanitizePath(entry.Name);
                    string fileName = Path.GetFileName(cleanName);

                    var indexed = new IndexedFile
                    {
                        Name = cleanName,
                        ArchivePath = pakPath,
                        Offset = entry.Offset,
                        Size = entry.Size
                    };

                    _allFiles.Add(indexed);
                    _fullPathIndex[cleanName] = indexed;

                    if (!string.IsNullOrEmpty(fileName))
                    {
                        if (!_fileNameIndex.TryGetValue(fileName, out var list))
                        {
                            list = new List<IndexedFile>();
                            _fileNameIndex[fileName] = list;
                        }
                        list.Add(indexed);
                    }
                }
            }

            var looseFiles = Directory.GetFiles(rootPath, "*", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in looseFiles)
            {
                string ext = Path.GetExtension(filePath);
                if (ext.Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".dir", StringComparison.OrdinalIgnoreCase))
                    continue;

                string relPath = TDRArchive.SanitizePath(Path.GetRelativePath(rootPath, filePath));
                string fileName = Path.GetFileName(relPath);

                var indexed = new IndexedFile
                {
                    Name = relPath,
                    ArchivePath = filePath,
                    IsLooseFile = true
                };

                _allFiles.Add(indexed);
                _fullPathIndex[relPath] = indexed;

                if (!string.IsNullOrEmpty(fileName))
                {
                    if (!_fileNameIndex.TryGetValue(fileName, out var list))
                    {
                        list = new List<IndexedFile>();
                        _fileNameIndex[fileName] = list;
                    }
                    list.Add(indexed);
                }
            }
        }

        public IReadOnlyList<IndexedFile> GetFiles() => _allFiles;

        /// <summary>
        /// Returns the ArchivePath (pak file path, or for loose files — the file's own path)
        /// for the given virtual path. Returns null if not found.
        /// </summary>
        public string? GetArchivePath(string virtualPath)
        {
            if (string.IsNullOrWhiteSpace(virtualPath)) return null;
            string clean = TDRArchive.SanitizePath(virtualPath);
            if (_fullPathIndex.TryGetValue(clean, out var e)) return e.ArchivePath;
            string fn = Path.GetFileName(clean);
            if (_fileNameIndex.TryGetValue(fn, out var list) && list.Count > 0)
            {
                return (list.FirstOrDefault(f => f.IsLooseFile) ?? list[0]).ArchivePath;
            }
            return null;
        }

        public bool FileExists(string virtualPath)
        {
            if (string.IsNullOrWhiteSpace(virtualPath)) return false;
            string clean = TDRArchive.SanitizePath(virtualPath);
            if (_fullPathIndex.ContainsKey(clean)) return true;
            string fileName = Path.GetFileName(clean);
            return _fileNameIndex.ContainsKey(fileName);
        }

        public byte[]? LoadFile(string virtualPath)
        {
            return LoadFileContext(virtualPath, null);
        }

        public byte[]? LoadFile(IndexedFile indexed)
        {
            if (indexed == null) return null;
            return ReadIndexedData(indexed);
        }

        public byte[]? LoadFileContext(string virtualPath, string? trackContext)
        {
            if (string.IsNullOrWhiteSpace(virtualPath)) return null;

            string clean = TDRArchive.SanitizePath(virtualPath);
            string fileName = Path.GetFileName(clean);

            // 1. Direct full path match
            if (_fullPathIndex.TryGetValue(clean, out var indexed))
            {
                return ReadIndexedData(indexed);
            }

            // If bare name is not in the index, no candidates exist
            if (!_fileNameIndex.TryGetValue(fileName, out var candidates) || candidates.Count == 0)
            {
                return null;
            }

            // 2. Track context-aware match (filter ONLY candidate list O(K) where K <= 5, not all files O(N)!)
            if (!string.IsNullOrWhiteSpace(trackContext))
            {
                string tCtx = trackContext.ToLowerInvariant();
                var trackMatch = candidates.FirstOrDefault(f => {
                    string normPath = f.Name.ToLowerInvariant();
                    string normArchive = f.ArchivePath.ToLowerInvariant();
                    return normPath.Contains($"tracks/{tCtx}") || normArchive.Contains($"tracks/{tCtx}") || normPath.Contains(tCtx);
                });

                if (trackMatch != null)
                {
                    return ReadIndexedData(trackMatch);
                }
            }

            // 3. Shared assets match (prefer loose files or global ASSETS/3D/TEXTURES over unrelated track archives)
            var sharedMatch = candidates.FirstOrDefault(f => {
                string normPath = f.Name.ToLowerInvariant();
                return !normPath.Contains("tracks/") || f.IsLooseFile;
            });

            if (sharedMatch != null)
            {
                return ReadIndexedData(sharedMatch);
            }

            // 4. Fallback to first available candidate (prefer loose, else first PAK)
            var fallback = candidates.FirstOrDefault(f => f.IsLooseFile) ?? candidates[0];
            return ReadIndexedData(fallback);
        }

        private byte[]? ReadIndexedData(IndexedFile indexed)
        {
            if (indexed.IsLooseFile)
            {
                if (File.Exists(indexed.ArchivePath))
                    return File.ReadAllBytes(indexed.ArchivePath);
                return null;
            }

            try
            {
                using var fs = new FileStream(indexed.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(indexed.Offset, SeekOrigin.Begin);
                byte[] raw = new byte[indexed.Size];
                fs.ReadExactly(raw, 0, (int)indexed.Size);
                return TDRArchive.DecompressZig(raw);
            }
            catch
            {
                return null;
            }
        }
    }
}
