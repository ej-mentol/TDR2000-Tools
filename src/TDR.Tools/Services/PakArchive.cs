using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TDR.PakLib;

namespace TDR.Tools.Services
{
    /// <summary>
    /// Represents a single TDR2000 .PAK + .DIR archive package.
    /// Encapsulates in-memory package manipulation, atomic serialization, Trie index generation,
    /// 4-byte alignment, extraction, packing, and defragmentation with structured ArchiveResult logging.
    /// </summary>
    public sealed class PakArchive : IDisposable
    {
        public sealed class Entry
        {
            public string VirtualPath { get; set; } = string.Empty;
            public byte[] Content { get; set; } = Array.Empty<byte>();
            public int Size => Content.Length;
        }

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public string? FilePath { get; private set; }

        public IReadOnlyCollection<string> Entries => _entries.Keys;
        public int Count => _entries.Count;

        public PakArchive(string? filePath = null)
        {
            FilePath = filePath;
        }

        public static PakArchive Create(string pakPath)
        {
            return new PakArchive(pakPath);
        }

        public static PakArchive Open(string pakPath)
        {
            var archive = new PakArchive(pakPath);
            archive.LoadFromDisk(pakPath);
            return archive;
        }

        public bool Contains(string virtualPath)
        {
            string clean = TDRArchive.SanitizePath(virtualPath);
            return _entries.ContainsKey(clean);
        }

        public byte[]? Get(string virtualPath)
        {
            string clean = TDRArchive.SanitizePath(virtualPath);
            return _entries.TryGetValue(clean, out var entry) ? entry.Content : null;
        }

        public string? GetText(string virtualPath, Encoding? encoding = null)
        {
            byte[]? data = Get(virtualPath);
            if (data == null) return null;
            return (encoding ?? Encoding.ASCII).GetString(data);
        }

        public ArchiveResult Add(string virtualPath, byte[] content)
        {
            if (string.IsNullOrWhiteSpace(virtualPath))
                return ArchiveResult.Fail("[!] Cannot add file with empty virtual path.");

            string clean = TDRArchive.SanitizePath(virtualPath);
            _entries[clean] = new Entry
            {
                VirtualPath = clean,
                Content = content ?? Array.Empty<byte>()
            };

            return ArchiveResult.Ok($"[+] Added '{clean}' ({_entries[clean].Size} bytes)", 1);
        }

        public ArchiveResult Add(string virtualPath, ReadOnlySpan<byte> content)
        {
            return Add(virtualPath, content.ToArray());
        }

        public ArchiveResult AddText(string virtualPath, string text, Encoding? encoding = null)
        {
            byte[] bytes = (encoding ?? Encoding.ASCII).GetBytes(text ?? string.Empty);
            return Add(virtualPath, bytes);
        }

        public ArchiveResult AddFolder(string diskFolderPath, string? virtualBasePrefix = null)
        {
            if (!Directory.Exists(diskFolderPath))
                return ArchiveResult.Fail($"[!] Directory '{diskFolderPath}' not found.");

            string baseFolder = Path.GetFileName(diskFolderPath);
            int added = 0;

            foreach (string file in Directory.GetFiles(diskFolderPath, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(diskFolderPath, file).Replace('\\', '/');
                string virtPath = !string.IsNullOrEmpty(virtualBasePrefix)
                    ? $"{virtualBasePrefix.TrimEnd('/')}/{rel}"
                    : (!string.IsNullOrEmpty(baseFolder) ? $"{baseFolder}/{rel}" : rel);

                Add(virtPath, File.ReadAllBytes(file));
                added++;
            }

            return ArchiveResult.Ok($"[+] Added folder '{baseFolder}' ({added} files)", added);
        }

        public ArchiveResult Remove(string virtualPath)
        {
            string clean = TDRArchive.SanitizePath(virtualPath);
            if (!_entries.Remove(clean))
            {
                return ArchiveResult.Fail($"[!] File '{clean}' not found in archive index.");
            }

            return ArchiveResult.Ok($"[+] Removed entry '{clean}' from archive index.", affectedCount: 1);
        }

        public ArchiveResult RemoveFolder(string virtualFolderPrefix)
        {
            string prefix = TDRArchive.SanitizePath(virtualFolderPrefix).TrimEnd('/') + "/";
            var toRemove = _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in toRemove)
            {
                _entries.Remove(k);
            }

            if (toRemove.Count == 0)
                return ArchiveResult.Fail($"[!] Folder '{virtualFolderPrefix}' not found in archive index.");

            return ArchiveResult.Ok($"[+] Removed folder '{virtualFolderPrefix}' ({toRemove.Count} entries).", toRemove.Count);
        }

        public ArchiveResult Rename(string oldVirtualPath, string newVirtualPath)
        {
            string oldClean = TDRArchive.SanitizePath(oldVirtualPath);
            string newClean = TDRArchive.SanitizePath(newVirtualPath);

            if (!_entries.TryGetValue(oldClean, out var entry))
                return ArchiveResult.Fail($"[!] Cannot rename: '{oldClean}' not found in archive index.");

            _entries.Remove(oldClean);
            entry.VirtualPath = newClean;
            _entries[newClean] = entry;

            return ArchiveResult.Ok($"[+] Renamed '{oldClean}' -> '{newClean}'", 1);
        }

        public ArchiveResult ExtractAll(string outputDirectory)
        {
            if (_entries.Count == 0)
                return ArchiveResult.Fail("[!] Archive is empty. Extraction cancelled.");

            try
            {
                Directory.CreateDirectory(outputDirectory);
                int extracted = 0;

                foreach (var entry in _entries.Values)
                {
                    string outPath = Path.Combine(outputDirectory, entry.VirtualPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    File.WriteAllBytes(outPath, entry.Content);
                    extracted++;
                }

                return ArchiveResult.Ok($"Extracted {extracted} files to {outputDirectory}", extracted);
            }
            catch (Exception ex)
            {
                return ArchiveResult.Fail($"[ERROR] Failed to extract archive: {ex.Message}");
            }
        }

        public ArchiveResult ExtractFile(string virtualPath, string destinationFilePath)
        {
            byte[]? data = Get(virtualPath);
            if (data == null)
                return ArchiveResult.Fail($"[!] File '{virtualPath}' not found in archive.");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
                File.WriteAllBytes(destinationFilePath, data);
                return ArchiveResult.Ok($"Extracted '{virtualPath}' ({data.Length} bytes) to {destinationFilePath}", 1);
            }
            catch (Exception ex)
            {
                return ArchiveResult.Fail($"[ERROR] Failed to extract '{virtualPath}': {ex.Message}");
            }
        }

        public ArchiveResult ExtractSubfolder(string virtualFolderPrefix, string destinationDirectory)
        {
            string prefix = TDRArchive.SanitizePath(virtualFolderPrefix).TrimEnd('/') + "/";
            int count = 0;

            try
            {
                foreach (var entry in _entries.Values)
                {
                    if (entry.VirtualPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string subRel = entry.VirtualPath.Substring(prefix.Length);
                        string outPath = Path.Combine(destinationDirectory, subRel);
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                        File.WriteAllBytes(outPath, entry.Content);
                        count++;
                    }
                }

                if (count == 0)
                    return ArchiveResult.Fail($"[!] No files found under prefix '{virtualFolderPrefix}'.");

                return ArchiveResult.Ok($"Extracted {count} files from '{virtualFolderPrefix}' to {destinationDirectory}", count);
            }
            catch (Exception ex)
            {
                return ArchiveResult.Fail($"[ERROR] Failed to extract folder '{virtualFolderPrefix}': {ex.Message}");
            }
        }

        public ArchiveResult Save(string? targetPakPath = null, bool compress = true, Action<string>? log = null, bool allowEmptyDeletion = true)
        {
            string pakPath = targetPakPath ?? FilePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(pakPath))
                return ArchiveResult.Fail("[!] No target path specified for Save.");

            return PackEntries(_entries.Values, pakPath, compress, log, allowEmptyDeletion);
        }

        private void LoadFromDisk(string pakPath)
        {
            _entries.Clear();
            if (!File.Exists(pakPath)) return;

            string dirPath = Path.ChangeExtension(pakPath, ".dir");
            if (!File.Exists(dirPath)) return;

            var entries = TDRArchive.ParseTrieIndex(dirPath);
            if (entries.Count == 0) return;

            using var pakStream = File.OpenRead(pakPath);
            foreach (var e in entries)
            {
                if (e.Offset + e.Size > pakStream.Length) continue;

                pakStream.Seek(e.Offset, SeekOrigin.Begin);
                byte[] rawBlock = new byte[e.Size];
                int read = pakStream.Read(rawBlock, 0, (int)e.Size);
                if (read < e.Size) continue;

                byte[] uncompressed = TDRArchive.DecompressZig(rawBlock);
                string cleanName = TDRArchive.SanitizePath(e.Name);

                _entries[cleanName] = new Entry
                {
                    VirtualPath = cleanName,
                    Content = uncompressed
                };
            }
        }

        public void Dispose()
        {
            _entries.Clear();
        }

        // --- Static Operations ---

        public static ArchiveResult PackDirectory(string inputDirectoryPath, string outputPakPath, bool compress = true, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(inputDirectoryPath) || !Directory.Exists(inputDirectoryPath))
            {
                string err = $"[!] Error: Input directory '{inputDirectoryPath}' does not exist.";
                log?.Invoke(err);
                return ArchiveResult.Fail(err);
            }

            var entries = new List<Entry>();
            string[] diskFiles = Directory.GetFiles(inputDirectoryPath, "*", SearchOption.AllDirectories);

            foreach (string file in diskFiles)
            {
                string relPath = Path.GetRelativePath(inputDirectoryPath, file).Replace('\\', '/');
                byte[] data = File.ReadAllBytes(file);
                entries.Add(new Entry { VirtualPath = relPath, Content = data });
            }

            return PackEntries(entries, outputPakPath, compress, log);
        }

        private static ArchiveResult PackEntries(IEnumerable<Entry> entries, string outputPakPath, bool compress = true, Action<string>? log = null, bool allowEmptyDeletion = true)
        {
            var entryList = entries.ToList();
            string outputDir = Path.GetDirectoryName(outputPakPath) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outputDir);

            string pakFile = Path.ChangeExtension(outputPakPath, ".pak");
            string dirFile = Path.ChangeExtension(outputPakPath, ".dir");

            if (entryList.Count == 0)
            {
                if (!allowEmptyDeletion)
                {
                    string msg = $"[!] Packing aborted: 0 files to pack and allowEmptyDeletion is false.";
                    log?.Invoke(msg);
                    return ArchiveResult.Fail(msg);
                }
                if (File.Exists(pakFile)) File.Delete(pakFile);
                if (File.Exists(dirFile)) File.Delete(dirFile);
                string emptyMsg = $"[+] Empty archive state: physical files '{Path.GetFileName(pakFile)}' and '{Path.GetFileName(dirFile)}' removed from disk.";
                log?.Invoke(emptyMsg);
                return ArchiveResult.Ok(emptyMsg, 0);
            }

            log?.Invoke($"[+] Packing {entryList.Count} file(s) into '{Path.GetFileName(pakFile)}' & '{Path.GetFileName(dirFile)}'...");

            string tmpPak = pakFile + ".tmp";
            string tmpDir = dirFile + ".tmp";

            try
            {
                var indexedFiles = new List<(string VirtualPath, uint Offset, uint Size)>();

                using (var pakStream = new FileStream(tmpPak, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    foreach (var file in entryList)
                    {
                        string normPath = TDRArchive.SanitizePath(file.VirtualPath);

                        byte[] header = compress
                            ? TDRArchive.CreateZigHeaderAuto(file.Content)
                            : TDRArchive.CreateZigHeader(file.Content, compress: false);
                        TDRArchive.WriteAligned(pakStream, header);

                        uint offset = (uint)(pakStream.Position - header.Length);
                        uint totalBlockSize = (uint)header.Length;
                        indexedFiles.Add((normPath, offset, totalBlockSize));
                    }
                }

                var dirEntries = indexedFiles.Select(f => new FileEntry
                {
                    Name = f.VirtualPath,
                    Offset = f.Offset,
                    Size = f.Size
                }).ToList();

                byte[] dirBytes = TDRArchive.SerializeTrieIndex(dirEntries);
                File.WriteAllBytes(tmpDir, dirBytes);

                if (File.Exists(pakFile)) File.SetAttributes(pakFile, FileAttributes.Normal);
                if (File.Exists(dirFile)) File.SetAttributes(dirFile, FileAttributes.Normal);

                File.Move(tmpPak, pakFile, overwrite: true);
                File.Move(tmpDir, dirFile, overwrite: true);

                string successMsg = $"[+] Successfully packed '{Path.GetFileName(pakFile)}' & '{Path.GetFileName(dirFile)}' ({entryList.Count} files).";
                log?.Invoke(successMsg);
                return ArchiveResult.Ok(successMsg, entryList.Count);
            }
            catch (Exception ex)
            {
                if (File.Exists(tmpPak)) try { File.Delete(tmpPak); } catch { }
                if (File.Exists(tmpDir)) try { File.Delete(tmpDir); } catch { }
                string errMsg = $"[ERROR] Failed to pack '{Path.GetFileName(pakFile)}': {ex.Message}";
                log?.Invoke(errMsg);
                return ArchiveResult.Fail(errMsg);
            }
        }

        public static async Task<ArchiveResult> DefragmentAsync(string pakPath, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(pakPath) || !File.Exists(pakPath))
                return ArchiveResult.Fail($"[!] PAK file '{pakPath}' does not exist.");

            string dirPath = Path.ChangeExtension(pakPath, ".dir");
            if (!File.Exists(dirPath))
                return ArchiveResult.Fail($"[!] Matching DIR file '{dirPath}' does not exist.");

            string archiveName = Path.GetFileName(pakPath);
            log?.Invoke($"[+] Starting defragmentation of '{archiveName}'...");

            return await Task.Run(() =>
            {
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

                    byte[] newDirBytes = TDRArchive.SerializeTrieIndex(newIdx);
                    File.WriteAllBytes(tmpDir, newDirBytes);

                    if (File.Exists(pakPath)) File.SetAttributes(pakPath, FileAttributes.Normal);
                    if (File.Exists(dirPath)) File.SetAttributes(dirPath, FileAttributes.Normal);

                    File.Move(tmpPak, pakPath, overwrite: true);
                    File.Move(tmpDir, dirPath, overwrite: true);

                    string doneMsg = $"[+] Rebuild & Defragmentation completed for '{archiveName}' ({newIdx.Count} active files).";
                    log?.Invoke(doneMsg);
                    return ArchiveResult.Ok(doneMsg, newIdx.Count);
                }
                catch (Exception ex)
                {
                    if (File.Exists(tmpPak)) try { File.Delete(tmpPak); } catch { }
                    if (File.Exists(tmpDir)) try { File.Delete(tmpDir); } catch { }
                    string failMsg = $"[ERROR] Failed to rebuild archive '{archiveName}': {ex.Message}";
                    log?.Invoke(failMsg);
                    return ArchiveResult.Fail(failMsg);
                }
            });
        }
    }
}
