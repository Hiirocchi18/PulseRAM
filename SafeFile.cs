using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PulseRAM
{
    /// <summary>
    /// File writes and deletes in PulseRAM's data folder. That folder can be changed by any program the user runs, so
    /// when PulseRAM is elevated a planted junction, symlink or hard link could otherwise redirect an admin write or
    /// delete to a system file. Elevated, every operation opens the file itself (never following a final link),
    /// then checks on the open handle that it is a plain, single-link file still inside the expected folder.
    /// Checking the handle (not the path) leaves no gap to swap things in between.
    /// When not elevated it simply uses the normal file APIs.
    /// </summary>
    public static class SafeFile
    {
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_APPEND_DATA = 0x0004;
        private const uint DELETE = 0x00010000;
        private const uint FILE_READ_ATTRIBUTES = 0x0080;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint OPEN_EXISTING = 3;
        private const uint OPEN_ALWAYS = 4;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder path, uint len, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public long CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_DISPOSITION_INFO { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref FILE_DISPOSITION_INFO info, uint size);

        private const int FileDispositionInfo = 4;

        /// <summary>Writes (or appends) text. Returns false and writes nothing if the target isn't safe.</summary>
        public static bool WriteAllText(string path, string text, bool append = false)
        {
            if (!InstallSecurity.IsElevated)
            {
                if (append) File.AppendAllText(path, text, Encoding.UTF8);
                else File.WriteAllText(path, text, Encoding.UTF8);
                return true;
            }

            // Check the folder first so a new file is never created through a redirected folder.
            if (!FolderIsReal(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "")) return false;
            using var h = CreateFileW(Path.GetFullPath(path), (append ? FILE_APPEND_DATA : GENERIC_WRITE) | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                FILE_SHARE_READ, IntPtr.Zero, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid || !IsPlainFileInDataFolder(h, path)) return false;
            using var fs = new FileStream(h, append ? FileAccess.Write : FileAccess.Write);
            if (append) fs.Seek(0, SeekOrigin.End);
            else fs.SetLength(0);
            var bytes = Encoding.UTF8.GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
            return true;
        }

        /// <summary>Deletes a file (a link is deleted itself, never its target). Missing files count as deleted.</summary>
        public static bool Delete(string path)
        {
            if (!InstallSecurity.IsElevated)
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            if (!File.Exists(path) && !Directory.Exists(path)) return true;
            using var h = CreateFileW(Path.GetFullPath(path), DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_READ,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid) return false;
            // Only the folder matters here: deleting a link (opened as the link itself) is harmless.
            if (!IsInDataFolder(h, path)) return false;
            var info = new FILE_DISPOSITION_INFO { DeleteFile = true };
            return SetFileInformationByHandle(h, FileDispositionInfo, ref info, 1);
        }

        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        private static bool FolderIsReal(string dir)
        {
            using var h = CreateFileW(dir, FILE_READ_ATTRIBUTES | SYNCHRONIZE, 0x7 /* share all */, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid || !GetFileInformationByHandle(h, out var fi)) return false;
            if ((fi.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) return false;
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n >= sb.Capacity) return false;
            string real = sb.ToString();
            if (real.StartsWith(@"\\?\", StringComparison.Ordinal)) real = real.Substring(4);
            string root = Path.GetFullPath(NativeMemoryEngine.DataDirectory).TrimEnd('\\');
            return string.Equals(real.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && (real.TrimEnd('\\') + "\\").StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPlainFileInDataFolder(SafeFileHandle h, string path)
        {
            if (!GetFileInformationByHandle(h, out var fi)) return false;
            if ((fi.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) return false; // a symlink, not a file
            if (fi.NumberOfLinks > 1) return false; // a hard link to some other file
            return IsInDataFolder(h, path);
        }

        /// <summary>The handle's real location must be the intended path (no junction anywhere above it).</summary>
        private static bool IsInDataFolder(SafeFileHandle h, string path)
        {
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n >= sb.Capacity) return false;
            string real = sb.ToString();
            if (real.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) real = @"\\" + real.Substring(8);
            else if (real.StartsWith(@"\\?\", StringComparison.Ordinal)) real = real.Substring(4);
            string expected = Path.GetFullPath(path);
            string root = Path.GetFullPath(NativeMemoryEngine.DataDirectory).TrimEnd('\\') + "\\";
            return string.Equals(real, expected, StringComparison.OrdinalIgnoreCase)
                && real.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MITIGATION_REDIRECTION_TRUST_POLICY { public uint Flags; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessMitigationPolicy(int policy, ref PROCESS_MITIGATION_REDIRECTION_TRUST_POLICY value, UIntPtr size);

        /// <summary>
        /// Windows 11 22H2+: makes this (elevated) process ignore junctions created by non-administrators
        /// ("RedirectionGuard"). Earlier Windows versions ignore the call; the handle checks above still apply.
        /// </summary>
        public static void EnableRedirectionGuard()
        {
            if (!InstallSecurity.IsElevated) return;
            try
            {
                var p = new PROCESS_MITIGATION_REDIRECTION_TRUST_POLICY { Flags = 1 /* EnforceRedirectionTrust */ };
                SetProcessMitigationPolicy(16 /* ProcessRedirectionTrustPolicy */, ref p, (UIntPtr)Marshal.SizeOf<PROCESS_MITIGATION_REDIRECTION_TRUST_POLICY>());
            }
            catch { }
        }
    }
}
