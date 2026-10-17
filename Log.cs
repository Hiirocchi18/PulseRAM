using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace PulseRAM
{
    /// <summary>
    /// Small, dependency-free file logger: %LocalAppData%\PulseRAM\logs\pulseram-yyyyMMdd.log.
    /// Keeps 7 days, caps each file at 2 MB, never throws.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private const long MaxFileBytes = 2 * 1024 * 1024;
        private const int KeepDays = 7;
        private static bool _pruned;

        public static string Directory => Path.Combine(NativeMemoryEngine.DataDirectory, "logs");

        private static string CurrentFile => Path.Combine(Directory, $"pulseram-{DateTime.Now:yyyyMMdd}.log");

        /// <summary>Marker present while PulseRAM runs; if it is still there at the next start, the last session crashed.</summary>
        private static string RunningMarker => Path.Combine(NativeMemoryEngine.DataDirectory, "running.marker");

        public static void Info(string message) => Write("INFO ", message, null);

        public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

        public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception? ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
                  .Append(level).Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
                  .Append(message);
                if (ex != null) sb.AppendLine().Append(ex);
                sb.AppendLine();

                lock (Gate)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    if (!_pruned) Prune();
                    var file = CurrentFile;
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > MaxFileBytes)
                    {
                        File.Move(file, Path.Combine(Directory, $"pulseram-{DateTime.Now:yyyyMMdd-HHmmss}.log"), true);
                    }
                    File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }

        private static void Prune()
        {
            _pruned = true;
            try
            {
                var cutoff = DateTime.Now.AddDays(-KeepDays);
                foreach (var f in new DirectoryInfo(Directory).GetFiles("pulseram-*.log").Where(f => f.LastWriteTime < cutoff))
                    f.Delete();
            }
            catch { }
        }

        /// <summary>
        /// Call once at startup. Returns true if the previous session ended without a clean shutdown.
        /// </summary>
        public static bool MarkSessionStart()
        {
            bool crashed = false;
            try
            {
                System.IO.Directory.CreateDirectory(NativeMemoryEngine.DataDirectory);
                crashed = File.Exists(RunningMarker);
                File.WriteAllText(RunningMarker, Environment.ProcessId.ToString());
            }
            catch { }
            return crashed;
        }

        public static void MarkSessionEnd()
        {
            try { if (File.Exists(RunningMarker)) File.Delete(RunningMarker); } catch { }
        }

        public static void OpenFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Directory}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
