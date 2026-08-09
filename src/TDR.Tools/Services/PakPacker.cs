using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TDR.PakLib;

namespace TDR.Tools.Services
{
    /// <summary>
    /// Service for building and repacking TDR2000 .PAK and .DIR archive files.
    /// Ensures 100% format compliance: Trie indexing, 4-byte alignment, and zIG/RAW block encryption.
    /// </summary>
    public static class PakPacker
    {
        public sealed class FileToPack
        {
            public string VirtualPath { get; set; } = string.Empty;
            public byte[] Content { get; set; } = Array.Empty<byte>();
        }

        /// <summary>
        /// Packs a directory on disk into target .PAK and .DIR files.
        /// </summary>
        public static bool PackDirectory(string inputDirectoryPath, string outputPakPath, bool compress = true, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(inputDirectoryPath) || !Directory.Exists(inputDirectoryPath))
            {
                log?.Invoke($"[!] Error: Input directory '{inputDirectoryPath}' does not exist.");
                return false;
            }

            var files = new List<FileToPack>();
            string[] diskFiles = Directory.GetFiles(inputDirectoryPath, "*", SearchOption.AllDirectories);

            foreach (string file in diskFiles)
            {
                string relPath = Path.GetRelativePath(inputDirectoryPath, file).Replace('\\', '/').ToLowerInvariant();
                byte[] data = File.ReadAllBytes(file);
                files.Add(new FileToPack { VirtualPath = relPath, Content = data });
            }

            return PackFiles(files, outputPakPath, compress, log);
        }

        /// <summary>
        /// Packs in-memory file entries into target .PAK and .DIR files.
        /// If files list is empty (0 files), physical files on disk are deleted.
        /// </summary>
        public static bool PackFiles(IEnumerable<FileToPack> fileEntries, string outputPakPath, bool compress = true, Action<string>? log = null)
        {
            var fileList = fileEntries.ToList();
            string outputDir = Path.GetDirectoryName(outputPakPath) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outputDir);

            string pakFile = Path.ChangeExtension(outputPakPath, ".pak");
            string dirFile = Path.ChangeExtension(outputPakPath, ".dir");

            // Lazy creation rule: if 0 files, clean up physical files on disk
            if (fileList.Count == 0)
            {
                if (File.Exists(pakFile)) File.Delete(pakFile);
                if (File.Exists(dirFile)) File.Delete(dirFile);
                log?.Invoke($"[+] Empty archive state: physical files '{Path.GetFileName(pakFile)}' and '{Path.GetFileName(dirFile)}' removed from disk.");
                return true;
            }

            log?.Invoke($"[+] Packing {fileList.Count} file(s) into '{Path.GetFileName(pakFile)}' & '{Path.GetFileName(dirFile)}'...");

            string tmpPak = pakFile + ".tmp";
            string tmpDir = dirFile + ".tmp";

            try
            {
                var indexedFiles = new List<(string VirtualPath, uint Offset, uint Size)>();

                using (var pakStream = new FileStream(tmpPak, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    foreach (var file in fileList)
                    {
                        string normPath = file.VirtualPath.Replace('\\', '/').ToLowerInvariant();
                        uint offset = (uint)pakStream.Position;

                        // Reuse the library's own zIG/RAW header (random key, verified format) instead of a
                        // separate, fixed-key reimplementation.
                        byte[] header = TDRArchive.CreateZigHeader(file.Content, compress);
                        long beforeWrite = pakStream.Position;
                        TDRArchive.WriteAligned(pakStream, header);

                        uint totalBlockSize = (uint)(pakStream.Position - beforeWrite);
                        indexedFiles.Add((normPath, offset, totalBlockSize));
                    }
                }

                // Write .DIR Trie index via the same serializer the library already uses (lib/TDR.PakLib),
                // instead of a duplicate, independently maintained trie writer.
                var pakFileEntries = indexedFiles
                    .Select(f => new FileEntry { Name = f.VirtualPath, Offset = f.Offset, Size = f.Size })
                    .ToList();
                byte[] dirBytes = TDRArchive.SerializeTrieIndex(pakFileEntries);
                File.WriteAllBytes(tmpDir, dirBytes);

                // Atomic Move: only overwrite original files once both .tmp files are 100% written
                File.Move(tmpPak, pakFile, overwrite: true);
                File.Move(tmpDir, dirFile, overwrite: true);

                log?.Invoke($"[✓] Archive successfully packed ({fileList.Count} files, PAK: {new FileInfo(pakFile).Length} bytes, DIR: {dirBytes.Length} bytes).");
                return true;
            }
            catch (Exception ex)
            {
                if (File.Exists(tmpPak)) try { File.Delete(tmpPak); } catch { }
                if (File.Exists(tmpDir)) try { File.Delete(tmpDir); } catch { }
                log?.Invoke($"[!] Exception while packing archive: {ex.Message}");
                return false;
            }
        }

    }
}
