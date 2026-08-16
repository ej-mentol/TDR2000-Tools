using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace TDR.Tools.Utilities
{
    public static class WindowsShell
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string? lpVerb;
            public string? lpFile;
            public string? lpParameters;
            public string? lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string? lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        private const int SW_SHOW = 5;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO execInfo);

        public static void ShowProperties(string path, IntPtr owner = default)
        {
            if (!OperatingSystem.IsWindows()) return;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var info = new SHELLEXECUTEINFO
                {
                    cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                    hwnd = owner,
                    lpVerb = "properties",
                    lpFile = path,
                    nShow = SW_SHOW,
                    fMask = SEE_MASK_INVOKEIDLIST
                };

                if (!ShellExecuteEx(ref info))
                {
                    int err = Marshal.GetLastWin32Error();
                    Services.LogService.Instance.Warn($"[Shell] ShowProperties failed for '{path}' (Win32 Error: {err})");
                }
            }
            catch (Exception ex)
            {
                Services.LogService.Instance.Error($"[Shell] ShowProperties exception for '{path}': {ex.Message}");
            }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

        public static void SendToRecycleBin(string path, bool permanent = false, bool confirm = false, IntPtr ownerHwnd = default)
        {
            if (string.IsNullOrEmpty(path)) return;

            // Win32 SHFileOperation requires paths WITHOUT trailing slashes!
            path = path.TrimEnd('/', '\\');

            if (!OperatingSystem.IsWindows())
            {
                if (permanent)
                {
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    else if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
                }
                else
                {
                    Services.LogService.Instance.Warn($"[Shell] Recycle bin is only supported on Windows. Soft deletion skipped for '{path}'.");
                }
                return;
            }

            try
            {
                // Double null-terminated string required for SHFileOperation
                string doubleNullPath = path + "\0\0";

                ushort flags = 0;
                if (!permanent)
                {
                    flags |= FOF_ALLOWUNDO; // Send to Recycle Bin
                }

                if (!confirm)
                {
                    flags |= FOF_NOCONFIRMATION; // Quiet move
                }

                var shf = new SHFILEOPSTRUCT
                {
                    hwnd = ownerHwnd,
                    wFunc = FO_DELETE,
                    pFrom = doubleNullPath,
                    fFlags = flags
                };

                int result = SHFileOperation(ref shf);
                if (result != 0 && !shf.fAnyOperationsAborted)
                {
                    if (permanent)
                    {
                        // Explicit permanent deletion requested: fallback to direct deletion
                        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                        else if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
                    }
                    else
                    {
                        // Soft deletion failed: do NOT permanently delete without user consent!
                        Services.LogService.Instance.Warn($"[Shell] Failed to move '{path}' to Recycle Bin (Win32 Error: {result}). Permanent deletion aborted to prevent data loss.");
                    }
                }
            }
            catch (Exception ex)
            {
                if (permanent)
                {
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    else if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
                }
                else
                {
                    Services.LogService.Instance.Error($"[Shell] Exception moving '{path}' to Recycle Bin: {ex.Message}. Permanent deletion aborted.");
                }
            }
        }
    }
}
