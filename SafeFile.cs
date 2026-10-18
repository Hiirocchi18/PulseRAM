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

        // FILETIME is two DWORDs (4-byte aligned): Pack = 4 keeps the native layout (52 bytes).
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
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

        private const uint CREATE_NEW = 1;
        private const int ERROR_ALREADY_EXISTS = 183;
        private const int FileRenameInfo = 3;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, IntPtr info, uint size);

        /// <summary>
        /// Writes (or appends) text. Elevated, a whole-file write goes to a new temporary sibling first and is then
        /// renamed over the target through the verified handle, so a crash never leaves a half-written file.
        /// Returns false (and leaves nothing behind) if the target isn't safe.
        /// </summary>
        public static bool WriteAllText(string path, string text, bool append = false)
        {
            if (!InstallSecurity.IsElevated)
            {
                if (append) File.AppendAllText(path, text, Encoding.UTF8);
                else File.WriteAllText(path, text, Encoding.UTF8);
                return true;
            }

            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full) ?? "";
            // Check the folder first so a new file is never created through a redirected folder.
            if (!FolderIsReal(dir)) return false;
            var bytes = Encoding.UTF8.GetBytes(text);

            if (append)
            {
                var h = CreateFileW(full, FILE_APPEND_DATA | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_READ,
                    IntPtr.Zero, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
                bool existed = Marshal.GetLastWin32Error() == ERROR_ALREADY_EXISTS;
                if (h.IsInvalid) return false;
                if (!IsPlainFileAt(h, full))
                {
                    if (!existed) DeleteByHandle(h); // don't leave a file we created somewhere unexpected
                    h.Dispose();
                    return false;
                }
                using var fs = new FileStream(h, FileAccess.Write);
                fs.Seek(0, SeekOrigin.End);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
                return true;
            }

            string tmp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
            var th = CreateFileW(tmp, GENERIC_WRITE | DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, 0, IntPtr.Zero, CREATE_NEW,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (th.IsInvalid) return false;
            var stream = new FileStream(th, FileAccess.Write);
            try
            {
                if (!IsPlainFileAt(th, tmp)) { DeleteByHandle(th); return false; }
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
                if (!RenameByHandle(th, full)) { DeleteByHandle(th); return false; }
                return true;
            }
            finally
            {
                stream.Dispose();
            }
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
            string full = Path.GetFullPath(path);
            using var h = CreateFileW(full, DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_READ,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid) return false;
            // Only the location matters here: deleting a link (opened as the link itself) is harmless.
            if (!IsAt(h, full)) return false;
            return DeleteByHandle(h);
        }

        private static bool DeleteByHandle(SafeFileHandle h)
        {
            var info = new FILE_DISPOSITION_INFO { DeleteFile = true };
            return SetFileInformationByHandle(h, FileDispositionInfo, ref info, 1);
        }

        /// <summary>FILE_RENAME_INFO (64-bit layout): flags @0, RootDirectory @8, FileNameLength @16, FileName @20.</summary>
        private static bool RenameByHandle(SafeFileHandle h, string target)
        {
            byte[] name = Encoding.Unicode.GetBytes(target);
            int size = 24 + name.Length;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
                Marshal.WriteInt32(buf, 0, 1);            // ReplaceIfExists
                Marshal.WriteIntPtr(buf, 8, IntPtr.Zero);  // RootDirectory
                Marshal.WriteInt32(buf, 16, name.Length);  // FileNameLength (bytes)
                Marshal.Copy(name, 0, buf + 20, name.Length);
                return SetFileInformationByHandle(h, FileRenameInfo, buf, (uint)size);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        // ---- Where the data folder really is ----
        // Moving C:\Users (or one profile) to another drive with an admin-made junction is legitimate. So the real
        // location of the data folder's parent is resolved once, accepting links only when they are owned by
        // SYSTEM / Administrators / TrustedInstaller (the same rule as RedirectionGuard). The data folder itself
        // and everything inside it must not be links.
        private static readonly object RootGate = new object();
        private static string? _dataDirKey;
        private static string? _realDataDir;

        private static readonly string[] TrustedLinkOwners = { "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" };

        private static string? RealDataDirectory()
        {
            string dataDir = Path.GetFullPath(NativeMemoryEngine.DataDirectory).TrimEnd('\\');
            lock (RootGate)
            {
                if (_dataDirKey == dataDir) return _realDataDir;
                _dataDirKey = dataDir;
                _realDataDir = ResolveTrusted(dataDir);
                return _realDataDir;
            }
        }

        private static string? ResolveTrusted(string dataDir)
        {
            try
            {
                string? parent = Path.GetDirectoryName(dataDir);
                if (string.IsNullOrEmpty(parent)) return null;
                for (string? d = parent; d != null; d = Path.GetDirectoryName(d))
                {
                    var di = new DirectoryInfo(d);
                    if (!di.Exists || (di.Attributes & FileAttributes.ReparsePoint) == 0) continue;
                    string? owner = di.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier))?.Value;
                    if (owner == null || Array.IndexOf(TrustedLinkOwners, owner) < 0) return null;
                }
                using var h = CreateFileW(parent, FILE_READ_ATTRIBUTES | SYNCHRONIZE, 0x7, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                string? real = FinalPath(h);
                return real == null ? null : real.TrimEnd('\\') + "\\" + Path.GetFileName(dataDir);
            }
            catch { return null; }
        }

        /// <summary>Maps a path inside the data folder to where it really is (null if it's outside or unresolvable).</summary>
        private static string? ExpectedRealPath(string fullPath)
        {
            string dataDir = Path.GetFullPath(NativeMemoryEngine.DataDirectory).TrimEnd('\\');
            string? realDir = RealDataDirectory();
            if (realDir == null) return null;
            string p = fullPath.TrimEnd('\\');
            if (string.Equals(p, dataDir, StringComparison.OrdinalIgnoreCase)) return realDir;
            if (!p.StartsWith(dataDir + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            return realDir + p.Substring(dataDir.Length);
        }

        private static string? FinalPath(SafeFileHandle h)
        {
            if (h.IsInvalid) return null;
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n >= sb.Capacity) return null;
            string real = sb.ToString();
            if (real.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + real.Substring(8);
            if (real.StartsWith(@"\\?\", StringComparison.Ordinal)) return real.Substring(4);
            return real;
        }

        private static bool FolderIsReal(string dir)
        {
            using var h = CreateFileW(dir, FILE_READ_ATTRIBUTES | SYNCHRONIZE, 0x7 /* share all */, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid || !GetFileInformationByHandle(h, out var fi)) return false;
            if ((fi.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) return false;
            return IsAt(h, dir);
        }

        private static bool IsPlainFileAt(SafeFileHandle h, string fullPath)
        {
            if (!GetFileInformationByHandle(h, out var fi)) return false;
            if ((fi.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) return false; // a symlink, not a file
            if (fi.NumberOfLinks > 1) return false; // a hard link to some other file
            return IsAt(h, fullPath);
        }

        /// <summary>The handle's real location must be exactly where this path should be inside the data folder.</summary>
        private static bool IsAt(SafeFileHandle h, string fullPath)
        {
            string? expected = ExpectedRealPath(Path.GetFullPath(fullPath));
            string? real = FinalPath(h);
            return expected != null && real != null &&
                   string.Equals(real.TrimEnd('\\'), expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
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
