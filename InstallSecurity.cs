using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PulseRAM
{
    /// <summary>
    /// Guards for running elevated. PulseRAM's data folder and (for per-user or portable copies) its EXE live where
    /// any program the user runs can write, so an elevated PulseRAM must not (1) let a logon task start an EXE from such
    /// a folder, (2) follow links planted in its data folder, or (3) hand its admin rights to a browser or Explorer.
    /// </summary>
    public static class InstallSecurity
    {
        public static bool IsElevated { get; } = DetectElevated();

        private static bool DetectElevated()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // Accounts that may write to a protected install folder.
        private static readonly string[] TrustedSids =
        {
            "S-1-5-18",      // LocalSystem
            "S-1-5-32-544",  // Administrators
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
            "S-1-3-0",       // CREATOR OWNER (only meaningful on inherit-only entries)
        };

        private const FileSystemRights WriteRights =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
            FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
            (FileSystemRights)0x10000000 /* GENERIC_ALL */ | (FileSystemRights)0x40000000 /* GENERIC_WRITE */;

        /// <summary>
        /// True only for an all-users install in Program Files whose folders and EXE/DLL can't be changed by
        /// non-administrators, so it is safe to start it elevated without a prompt. Never true for portable copies.
        /// </summary>
        public static bool IsAdminOnlyWritable(string exePath)
        {
            try
            {
                if (NativeMemoryEngine.IsPortable) return false;
                string full = Path.GetFullPath(exePath);
                string? dir = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(dir)) return false;

                var roots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                }.Where(r => r.Length > 3).Select(r => r.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string? root = roots.FirstOrDefault(r => dir.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
                if (root == null) return false;

                // The install folder and every folder up to Program Files: no links, no write access for others.
                for (string? d = dir; d != null && d.Length >= root.Length; d = Path.GetDirectoryName(d))
                {
                    var di = new DirectoryInfo(d);
                    if (!di.Exists || (di.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if (GrantsWriteToOthers(di.GetAccessControl())) return false;
                }
                // Every file and folder PulseRAM loads from: the EXE, its DLLs (.NET and Windows App SDK are
                // bundled), resources and sub-folders. One writable DLL would be enough to take over an elevated start.
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions
                         { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    FileSystemSecurity sec = entry is DirectoryInfo d2 ? d2.GetAccessControl() : ((FileInfo)entry).GetAccessControl();
                    if (GrantsWriteToOthers(sec)) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static readonly string[] TrustedOwners = { "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" };

        private static bool GrantsWriteToOthers(FileSystemSecurity security)
        {
            // The owner can always rewrite the permissions, so it must be SYSTEM, Administrators or TrustedInstaller.
            string? owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
            if (owner == null || !TrustedOwners.Contains(owner, StringComparer.OrdinalIgnoreCase)) return true;

            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & WriteRights) == 0) continue;
                string sid = rule.IdentityReference.Value;
                if (TrustedSids.Contains(sid, StringComparer.OrdinalIgnoreCase)) continue;
                return true; // includes inherit-only grants: they'd apply to files added later
            }
            return false;
        }

        /// <summary>
        /// When elevated, refuses a data file whose folder (or the file itself) is a junction/symlink: a program
        /// running as the user could otherwise point PulseRAM's data folder somewhere else and make the elevated
        /// process overwrite or delete files there. Always true when not elevated.
        /// </summary>
        public static bool IsSafeDataPath(string filePath)
        {
            if (!IsElevated) return true;
            try
            {
                string full = Path.GetFullPath(filePath);
                var fi = new FileInfo(full);
                if (fi.Exists && (fi.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                string stop = Path.GetFullPath(Path.GetDirectoryName(NativeMemoryEngine.DataDirectory) ?? NativeMemoryEngine.DataDirectory);
                for (string? d = Path.GetDirectoryName(full); d != null && d.Length > stop.Length; d = Path.GetDirectoryName(d))
                {
                    var di = new DirectoryInfo(d);
                    if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        /// <summary>
        /// Opens a web page or folder. When PulseRAM is elevated it asks the (non-elevated) Explorer shell to open it,
        /// so the browser or folder window doesn't inherit administrator rights; without a shell it does nothing.
        /// </summary>
        public static bool OpenAsUser(string target)
        {
            try
            {
                if (!IsElevated)
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    return true;
                }
                if (GetShellWindow() == IntPtr.Zero) return false;
                // Explorer splits its command line at commas, so pass folders as a file:// URI (commas escaped).
                string arg = target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? target
                    : new Uri(Path.GetFullPath(target)).AbsoluteUri.Replace(",", "%2C");
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                {
                    ArgumentList = { arg },
                    UseShellExecute = false
                });
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not open {target}", ex);
                return false;
            }
        }
    }
}
