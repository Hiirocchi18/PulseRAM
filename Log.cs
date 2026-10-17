using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace PulseRAM
{
    /// <summary>
    /// Small, dependency-free file logger: %LocalAppData%\PulseRAM\logs\pulseram-yyyyMMdd.log.
    /// Keeps 7 days and at most ~20 MB in total, rolls files at 2 MB, collapses an identical message repeated
    /// within a minute into one line with a count, and never throws.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private const long MaxFileBytes = 2 * 1024 * 1024;
        private const int KeepDays = 7;
        private const long MaxFolderBytes = 20 * 1024 * 1024;
        private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(60);
        private static DateTime _prunedOn = DateTime.MinValue;
        private static readonly Dictionary<string, (DateTime Last, int Suppressed)> Recent = new Dictionary<string, (DateTime, int)>();

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
                lock (Gate)
                {
                    // Collapse repeats (e.g. a tick that fails every 1.5 s) so the log can't balloon.
                    var now = DateTime.Now;
                    string key = level + message + (ex?.GetType().FullName ?? "");
                    int suppressed = 0;
                    if (Recent.TryGetValue(key, out var r))
                    {
                        if (now - r.Last < RepeatWindow)
                        {
                            Recent[key] = (r.Last, r.Suppressed + 1);
                            return;
                        }
                        suppressed = r.Suppressed;
                    }
                    Recent[key] = (now, 0);
                    if (Recent.Count > 200) Recent.Clear();

                    var sb = new StringBuilder();
                    sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
                      .Append(level).Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
                      .Append(message);
                    if (suppressed > 0) sb.Append($" (repeated {suppressed} more time(s) in the last minute)");
                    if (ex != null) sb.AppendLine().Append(ex);
                    sb.AppendLine();

                    System.IO.Directory.CreateDirectory(Directory);
                    if (_prunedOn != now.Date) Prune();
                    var file = CurrentFile;
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > MaxFileBytes)
                    {
                        File.Move(file, Path.Combine(Directory, $"pulseram-{DateTime.Now:yyyyMMdd-HHmmss}.log"), true);
                        Prune(); // enforce the folder cap as files roll
                    }
                    File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }

        /// <summary>Runs once per day (tray apps run for weeks): drops old files, then the oldest until under the size cap.</summary>
        private static void Prune()
        {
            _prunedOn = DateTime.Now.Date;
            try
            {
                var cutoff = DateTime.Now.AddDays(-KeepDays);
                var files = new DirectoryInfo(Directory).GetFiles("pulseram-*.log").OrderBy(f => f.LastWriteTime).ToList();
                foreach (var f in files.Where(f => f.LastWriteTime < cutoff).ToList())
                {
                    f.Delete();
                    files.Remove(f);
                }
                long total = files.Sum(f => f.Length);
                foreach (var f in files)
                {
                    if (total <= MaxFolderBytes) break;
                    total -= f.Length;
                    f.Delete();
                }
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
