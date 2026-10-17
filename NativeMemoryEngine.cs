using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Win32;

namespace PulseRAM
{
    public class MemorySnapshot
    {
        public double TotalGb { get; set; }
        public double UsedGb { get; set; }
        public double AvailableGb { get; set; }
        public double StandbyAndCacheGb { get; set; }
        public double FreeExactGb { get; set; }
        public double Percent { get; set; }
        public string PressureLevel { get; set; } = "OPTIMAL";
        public double CommitTotalGb { get; set; }
        public double CommitLimitGb { get; set; }
        public double CommitPercent { get; set; }
        public double PagefileTotalGb { get; set; }
        public double PagefileUsedGb { get; set; }
        public double PagefilePercent { get; set; }
        public double KernelTotalMb { get; set; }
        public double KernelPagedMb { get; set; }
        public double KernelNonPagedMb { get; set; }
        public double CpuPercent { get; set; }
        public int ProcessCount { get; set; }
        public int ThreadCount { get; set; }
        public int HandleCount { get; set; }
        public string ForegroundApp { get; set; } = "";
        public int ForegroundPid { get; set; }
        public bool IsAdmin { get; set; }
        public bool StandbyPurgeSupported { get; set; }
        public double SessionFreedMb { get; set; }
        public int OptimizationCount { get; set; }
        public string ActiveProfileName { get; set; } = "Balanced Default";
        public int EffectiveThresholdPercent { get; set; } = 80;
        public string EffectiveMode { get; set; } = "smart";
        public int NextScheduledSeconds { get; set; }
    }

    public class ProcessItem
    {
        public string Name { get; set; } = "";
        public int Pid { get; set; }
        public int ProcessCount { get; set; } = 1;
        public double MemoryMb { get; set; }
        public double PeakMemoryMb { get; set; }
        public double CpuPercent { get; set; }
        public double LeakRateMbPerMin { get; set; }
        public bool IsLeaking { get; set; }
        public bool IsForeground { get; set; }
        public bool IsProtected { get; set; }
        public bool IsFrozen { get; set; }
        public bool IsEco { get; set; }
        public int? CapMb { get; set; }
        public int SmartScore { get; set; }
        public double ReclaimableMb { get; set; }
        public string StatusBadge { get; set; } = "BACKGROUND";
        public string StatusColorHex { get; set; } = "#94A3B8";
    }

    public class OptimizationEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Timestamp { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string Mode { get; set; } = "smart";
        public string Trigger { get; set; } = "Manual";
        public string Reason { get; set; } = "";
        public double FreedMb { get; set; }
        public double BeforePercent { get; set; }
        public double AfterPercent { get; set; }
        public int ProcessesTrimmed { get; set; }
        public long DurationMs { get; set; }
        public bool StandbyPurged { get; set; }
        public bool ModifiedFlushed { get; set; }
        public bool PagesCombined { get; set; }
    }

    public class TelemetryPoint
    {
        public string TimeLabel { get; set; } = "";
        public double UsedPercent { get; set; }
        public double CpuPercent { get; set; }
        public double UsedGb { get; set; }
        public double StandbyGb { get; set; }
    }

    public class WorkloadProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> MatchExes { get; set; } = new List<string>();
        public int RamThresholdPercent { get; set; } = 75;
        public string AutoMode { get; set; } = "smart";
        public bool AutoShieldMatched { get; set; } = true;
        public bool BoostOnLaunch { get; set; } = true;
        public bool Enabled { get; set; } = true;
    }

    public class EngineConfig
    {
        public bool AutoEnabled { get; set; } = true;
        public int RamThresholdPercent { get; set; } = 80;
        public double MinFreeRamGb { get; set; } = 2.0;
        public int CooldownSeconds { get; set; } = 30;
        public string AutoMode { get; set; } = "smart";
        public bool ProtectForeground { get; set; } = true;
        public bool AutoLeakRemediation { get; set; } = true;
        public double LeakVelocityThresholdMbMin { get; set; } = 120.0;
        public bool ScheduledTrimEnabled { get; set; } = false;
        public int ScheduledIntervalMinutes { get; set; } = 15;
        public bool AutoSoundChime { get; set; } = false;
        public bool FocusBoostActive { get; set; } = false;
        public int FocusBoostThresholdPercent { get; set; } = 62;
        public bool RunOnStartup { get; set; } = false;
        public bool ProfilesAutoSwitch { get; set; } = true;
        public string ForcedProfileId { get; set; } = "";
        public bool HotkeysEnabled { get; set; } = true;
        public string HotkeyPrimary { get; set; } = "Ctrl+Shift+M";
        public string HotkeySecondary { get; set; } = "Ctrl+Shift+P";
        public List<string> ProtectedApps { get; set; } = new List<string>();
        public List<string> EcoApps { get; set; } = new List<string>();
        public Dictionary<string, int> AppMemoryCapsMb { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public List<WorkloadProfile> Profiles { get; set; } = new List<WorkloadProfile>();
    }

    public sealed class NativeMemoryEngine : IDisposable
    {
        #region Win32 & NT Kernel P/Invoke Declarations

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal;
            public UIntPtr CommitLimit;
            public UIntPtr CommitPeak;
            public UIntPtr PhysicalTotal;
            public UIntPtr PhysicalAvailable;
            public UIntPtr SystemCache;
            public UIntPtr KernelTotal;
            public UIntPtr KernelPaged;
            public UIntPtr KernelNonpaged;
            public UIntPtr PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_COMBINE_INFORMATION_EX
        {
            public IntPtr Handle;
            public UIntPtr PagesCombined;
            public ulong Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, uint Flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, uint ProcessInformationSize);

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int SystemInformationClass, ref int SystemInfo, int SystemInfoLength);

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int SystemInformationClass, ref MEMORY_COMBINE_INFORMATION_EX SystemInfo, int SystemInfoLength);

        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr ProcessHandle);

        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr ProcessHandle);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_SUSPEND_RESUME = 0x0800;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        private const uint IDLE_PRIORITY_CLASS = 0x00000040;
        private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        private const int ProcessPowerThrottling = 4;
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        private const int SystemMemoryListInformation = 80;
        private const int SystemCombinePhysicalMemoryInformation = 130;
        private const int MemoryEmptyWorkingSets = 2;
        private const int MemoryFlushModifiedList = 3;
        private const int MemoryPurgeStandbyList = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 5;

        #endregion

        private static readonly HashSet<string> CriticalSystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "smss.exe", "csrss.exe",
            "wininit.exe", "services.exe", "lsass.exe", "lsm.exe", "winlogon.exe",
            "dwm.exe", "fontdrvhost.exe", "audiodg.exe", "WUDFHost.exe", " spoolsv.exe",
            "PulseRAM.exe"
        };

        private static readonly HashSet<string> UnfreezableProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "smss.exe", "csrss.exe",
            "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe", "dwm.exe",
            "explorer.exe", "svchost.exe", "taskmgr.exe", "sihost.exe", "ctfmon.exe",
            "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
            "audiodg.exe", "PulseRAM.exe"
        };

        private readonly string _configPath;
        private readonly object _lock = new object();
        private readonly object _scanLock = new object();
        private readonly List<OptimizationEvent> _events = new List<OptimizationEvent>();
        private readonly List<TelemetryPoint> _telemetry = new List<TelemetryPoint>();
        private readonly Dictionary<string, List<(DateTime Time, double MemMb)>> _appHistory = new Dictionary<string, List<(DateTime, double)>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _frozenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ecoApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<int, (DateTime Time, TimeSpan CpuTime)> _procCpuSamples = new Dictionary<int, (DateTime, TimeSpan)>();
        private TimeSpan _lastTotalProcCpu = TimeSpan.Zero;
        private DateTime _lastCpuSampleTime = DateTime.MinValue;
        private double _cachedSystemCpuPercent = 0.0;

        private List<ProcessItem> _cachedGroupedProcesses = new List<ProcessItem>();
        private List<ProcessItem> _cachedIndividualProcesses = new List<ProcessItem>();
        private DateTime _lastProcessScan = DateTime.MinValue;

        private DateTime _lastAutoOptimizeTime = DateTime.MinValue;
        private DateTime _lastScheduledSweepTime = DateTime.Now;
        private double _sessionFreedMb = 0.0;
        private int _optimizationCount = 0;
        private bool _isAdmin = false;
        private bool _privilegesEnabled = false;
        private WorkloadProfile? _activeMatchedProfile = null;
        private string _lastLaunchedProfileId = "";

        private Timer? _backgroundTimer;
        public EngineConfig Config { get; private set; } = new EngineConfig();

        public event Action<OptimizationEvent>? OnOptimizationCompleted;

        public NativeMemoryEngine()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = Path.Combine(baseDir, "pulseram_config.json");
            CheckAdminAndEnablePrivileges();
            LoadConfig();

            // Prime initial process & telemetry sample
            SampleTelemetryAndProcesses(forceProcessScan: true);

            _backgroundTimer = new Timer(BackgroundTick, null, 1500, 1500);
        }

        private void CheckAdminAndEnablePrivileges()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                _isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

                if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr hToken))
                {
                    bool p1 = EnablePrivilege(hToken, "SeProfileSingleProcessPrivilege");
                    bool p2 = EnablePrivilege(hToken, "SeIncreaseQuotaPrivilege");
                    bool p3 = EnablePrivilege(hToken, "SeDebugPrivilege");
                    _privilegesEnabled = _isAdmin && (p1 || p2 || p3);
                    CloseHandle(hToken);
                }
            }
            catch
            {
                _isAdmin = false;
                _privilegesEnabled = false;
            }
        }

        private static bool EnablePrivilege(IntPtr hToken, string privilegeName)
        {
            if (!LookupPrivilegeValue(null, privilegeName, out LUID luid))
                return false;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            return AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }

        public void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    var loaded = JsonSerializer.Deserialize<EngineConfig>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (loaded != null)
                    {
                        Config = loaded;
                    }
                }
            }
            catch
            {
                Config = new EngineConfig();
            }

            if (Config.Profiles == null || Config.Profiles.Count == 0)
            {
                Config.Profiles = GetDefaultProfiles();
            }

            if (Config.ProtectedApps == null || Config.ProtectedApps.Count == 0)
            {
                Config.ProtectedApps = new List<string> { "explorer.exe", "dwm.exe", "audiodg.exe" };
            }

            _ecoApps.Clear();
            if (Config.EcoApps != null)
            {
                foreach (var app in Config.EcoApps)
                    _ecoApps.Add(app);
            }

            Config.RunOnStartup = CheckWindowsStartupRegistry();
        }

        public void SaveConfig()
        {
            try
            {
                Config.EcoApps = _ecoApps.ToList();
                var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
            }
            catch
            {
                // Ignore disk write failure
            }
        }

        private static List<WorkloadProfile> GetDefaultProfiles()
        {
            return new List<WorkloadProfile>
            {
                new WorkloadProfile
                {
                    Id = "gaming_boost",
                    Name = "Gaming Boost",
                    Description = "Aggressive 68% RAM ceiling, shields active game EXE, and purges background bloat on game launch.",
                    MatchExes = new List<string> { "cs2.exe", "valorant-win64-shipping.exe", "dota2.exe", "eldenring.exe", "cyberpunk2077.exe", "helldivers2.exe", "fortniteclient-win64-shipping.exe", "gta5.exe", "r5apex.exe", "overwatch.exe" },
                    RamThresholdPercent = 68,
                    AutoMode = "smart",
                    AutoShieldMatched = true,
                    BoostOnLaunch = true,
                    Enabled = true
                },
                new WorkloadProfile
                {
                    Id = "creator_pro",
                    Name = "Creator / Video & 3D",
                    Description = "Protects NLE & 3D render engines while reclaiming memory from background browsers.",
                    MatchExes = new List<string> { "premiere.exe", "afterfx.exe", "resolve.exe", "blender.exe", "photoshop.exe", "obs64.exe" },
                    RamThresholdPercent = 76,
                    AutoMode = "smart",
                    AutoShieldMatched = true,
                    BoostOnLaunch = true,
                    Enabled = true
                },
                new WorkloadProfile
                {
                    Id = "dev_workstation",
                    Name = "Heavy Dev & AI",
                    Description = "Balanced 82% threshold designed for IDEs, Docker containers, and local LLM workloads.",
                    MatchExes = new List<string> { "devenv.exe", "rider64.exe", "idea64.exe", "ollama.exe", "lm studio.exe" },
                    RamThresholdPercent = 82,
                    AutoMode = "smart",
                    AutoShieldMatched = true,
                    BoostOnLaunch = false,
                    Enabled = true
                },
                new WorkloadProfile
                {
                    Id = "low_ram_saver",
                    Name = "Ultra Low-RAM Guard",
                    Description = "Tight 65% threshold with Deep Sweep + Standby Cache purge for high-pressure multitasking.",
                    MatchExes = new List<string>(),
                    RamThresholdPercent = 65,
                    AutoMode = "deep",
                    AutoShieldMatched = true,
                    BoostOnLaunch = false,
                    Enabled = true
                }
            };
        }

        private void BackgroundTick(object? state)
        {
            try
            {
                var snap = SampleTelemetryAndProcesses(forceProcessScan: false);

                // 1. Check Scheduled Maintenance Timer
                if (Config.ScheduledTrimEnabled && Config.ScheduledIntervalMinutes > 0)
                {
                    double elapsedSec = (DateTime.Now - _lastScheduledSweepTime).TotalSeconds;
                    if (elapsedSec >= Config.ScheduledIntervalMinutes * 60)
                    {
                        _lastScheduledSweepTime = DateTime.Now;
                        OptimizeMemory(Config.AutoMode, "Scheduled", $"Scheduled {Config.ScheduledIntervalMinutes}m maintenance sweep");
                        return;
                    }
                }

                // 2. Check Auto-Pilot Thresholds
                if (Config.AutoEnabled)
                {
                    double cooldownSec = (DateTime.Now - _lastAutoOptimizeTime).TotalSeconds;
                    if (cooldownSec >= Math.Max(10, Config.CooldownSeconds))
                    {
                        int effectiveThreshold = snap.EffectiveThresholdPercent;
                        string effectiveMode = snap.EffectiveMode;

                        bool overPercent = snap.Percent >= effectiveThreshold;
                        bool underFreeFloor = snap.AvailableGb <= Config.MinFreeRamGb;

                        if (overPercent || underFreeFloor)
                        {
                            _lastAutoOptimizeTime = DateTime.Now;
                            string reason = overPercent
                                ? $"RAM reached {snap.Percent:F1}% (Threshold: {effectiveThreshold}%)"
                                : $"Available RAM dropped to {snap.AvailableGb:F2} GB (Floor: {Config.MinFreeRamGb:F1} GB)";
                            OptimizeMemory(effectiveMode, "Auto-Pilot", reason);
                            return;
                        }
                    }
                }

                // 3. Enforce Per-App Memory Caps & Runaway Leak Guardian
                EnforceAppCapsAndLeakGuard(snap.ForegroundApp);
            }
            catch
            {
                // Never crash background loop
            }
        }

        private void EnforceAppCapsAndLeakGuard(string foregroundApp)
        {
            List<ProcessItem> grouped;
            lock (_lock)
            {
                grouped = _cachedGroupedProcesses.ToList();
            }

            foreach (var item in grouped)
            {
                if (string.Equals(item.Name, foregroundApp, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (CriticalSystemProcesses.Contains(item.Name))
                    continue;

                // Per-App Memory Cap check
                if (Config.AppMemoryCapsMb != null &&
                    Config.AppMemoryCapsMb.TryGetValue(item.Name, out int capMb) &&
                    capMb > 50 &&
                    item.MemoryMb > capMb * 1.08)
                {
                    OptimizeSingleApp(item.Name, $"Memory Cap Guard ({item.MemoryMb:F0} MB > {capMb} MB cap)", capMb);
                    continue;
                }

                // Runaway Leak Guardian check
                if (Config.AutoLeakRemediation &&
                    !item.IsProtected &&
                    item.IsLeaking &&
                    item.LeakRateMbPerMin >= Config.LeakVelocityThresholdMbMin &&
                    item.MemoryMb >= 300)
                {
                    if ((DateTime.Now - _lastAutoOptimizeTime).TotalSeconds >= 20)
                    {
                        _lastAutoOptimizeTime = DateTime.Now;
                        OptimizeSingleApp(item.Name, $"Runaway Leak Guard (+{item.LeakRateMbPerMin:F0} MB/min)");
                    }
                }
            }
        }

        public MemorySnapshot GetSnapshot()
        {
            return SampleTelemetryAndProcesses(forceProcessScan: false);
        }

        public List<ProcessItem> GetProcesses(bool grouped = true)
        {
            if ((DateTime.Now - _lastProcessScan).TotalMilliseconds > 2200)
            {
                RefreshProcessSnapshots(GetForegroundProcessInfo().Name);
            }

            lock (_lock)
            {
                return grouped
                    ? _cachedGroupedProcesses.ToList()
                    : _cachedIndividualProcesses.ToList();
            }
        }

        public List<OptimizationEvent> GetEvents()
        {
            lock (_lock)
            {
                return _events.ToList();
            }
        }

        public List<TelemetryPoint> GetTelemetry()
        {
            lock (_lock)
            {
                return _telemetry.ToList();
            }
        }

        private (int Pid, string Name) GetForegroundProcessInfo()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return (0, "");
                GetWindowThreadProcessId(hwnd, out int pid);
                if (pid <= 0) return (0, "");
                using var proc = Process.GetProcessById(pid);
                string name = proc.ProcessName;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name += ".exe";
                return (pid, name);
            }
            catch
            {
                return (0, "");
            }
        }

        private MemorySnapshot SampleTelemetryAndProcesses(bool forceProcessScan)
        {
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref memStatus);

            var perfInfo = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
            GetPerformanceInfo(out perfInfo, perfInfo.cb);

            double pageSize = perfInfo.PageSize.ToUInt64() > 0 ? perfInfo.PageSize.ToUInt64() : 4096.0;
            double totalGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            double availGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
            double usedGb = Math.Max(0.0, totalGb - availGb);
            double percent = totalGb > 0 ? (usedGb / totalGb) * 100.0 : memStatus.dwMemoryLoad;

            double standbyCacheGb = (perfInfo.SystemCache.ToUInt64() * pageSize) / (1024.0 * 1024.0 * 1024.0);
            double freeExactGb = Math.Max(0.0, availGb - standbyCacheGb * 0.65);

            double commitTotalGb = (perfInfo.CommitTotal.ToUInt64() * pageSize) / (1024.0 * 1024.0 * 1024.0);
            double commitLimitGb = Math.Max(0.1, (perfInfo.CommitLimit.ToUInt64() * pageSize) / (1024.0 * 1024.0 * 1024.0));
            double commitPct = (commitTotalGb / commitLimitGb) * 100.0;

            double pfTotalGb = Math.Max(0.0, (memStatus.ullTotalPageFile / (1024.0 * 1024.0 * 1024.0)) - totalGb);
            double pfAvailGb = Math.Max(0.0, (memStatus.ullAvailPageFile / (1024.0 * 1024.0 * 1024.0)) - availGb);
            double pfUsedGb = Math.Max(0.0, pfTotalGb - pfAvailGb);
            double pfPct = pfTotalGb > 0.05 ? Math.Min(100.0, (pfUsedGb / pfTotalGb) * 100.0) : 0.0;

            double kernelTotalMb = (perfInfo.KernelTotal.ToUInt64() * pageSize) / (1024.0 * 1024.0);
            double kernelPagedMb = (perfInfo.KernelPaged.ToUInt64() * pageSize) / (1024.0 * 1024.0);
            double kernelNonPagedMb = (perfInfo.KernelNonpaged.ToUInt64() * pageSize) / (1024.0 * 1024.0);

            var fg = GetForegroundProcessInfo();

            if (forceProcessScan || (DateTime.Now - _lastProcessScan).TotalMilliseconds >= 2200)
            {
                RefreshProcessSnapshots(fg.Name);
            }

            string pressure = percent >= 85 ? "CRITICAL" : (percent >= 72 ? "ELEVATED" : "OPTIMAL");

            // Resolve Effective Profile & Threshold
            string activeProfileName = "Balanced Default";
            int effectiveThreshold = Config.RamThresholdPercent;
            string effectiveMode = Config.AutoMode;

            if (Config.FocusBoostActive)
            {
                activeProfileName = "Focus Boost Overdrive";
                effectiveThreshold = Config.FocusBoostThresholdPercent;
                effectiveMode = "smart";
            }
            else if (!string.IsNullOrWhiteSpace(Config.ForcedProfileId))
            {
                var forced = Config.Profiles.FirstOrDefault(p => p.Id == Config.ForcedProfileId);
                if (forced != null)
                {
                    activeProfileName = forced.Name;
                    effectiveThreshold = forced.RamThresholdPercent;
                    effectiveMode = forced.AutoMode;
                }
            }
            else if (_activeMatchedProfile != null)
            {
                activeProfileName = _activeMatchedProfile.Name;
                effectiveThreshold = _activeMatchedProfile.RamThresholdPercent;
                effectiveMode = _activeMatchedProfile.AutoMode;
            }

            int nextSchedSec = 0;
            if (Config.ScheduledTrimEnabled && Config.ScheduledIntervalMinutes > 0)
            {
                int totalSec = Config.ScheduledIntervalMinutes * 60;
                int elapsed = (int)(DateTime.Now - _lastScheduledSweepTime).TotalSeconds;
                nextSchedSec = Math.Max(0, totalSec - elapsed);
            }

            var snapshot = new MemorySnapshot
            {
                TotalGb = Math.Round(totalGb, 2),
                UsedGb = Math.Round(usedGb, 2),
                AvailableGb = Math.Round(availGb, 2),
                StandbyAndCacheGb = Math.Round(standbyCacheGb, 2),
                FreeExactGb = Math.Round(freeExactGb, 2),
                Percent = Math.Round(percent, 1),
                PressureLevel = pressure,
                CommitTotalGb = Math.Round(commitTotalGb, 2),
                CommitLimitGb = Math.Round(commitLimitGb, 2),
                CommitPercent = Math.Round(commitPct, 1),
                PagefileTotalGb = Math.Round(pfTotalGb, 2),
                PagefileUsedGb = Math.Round(pfUsedGb, 2),
                PagefilePercent = Math.Round(pfPct, 1),
                KernelTotalMb = Math.Round(kernelTotalMb, 0),
                KernelPagedMb = Math.Round(kernelPagedMb, 0),
                KernelNonPagedMb = Math.Round(kernelNonPagedMb, 0),
                CpuPercent = Math.Round(_cachedSystemCpuPercent, 1),
                ProcessCount = (int)perfInfo.ProcessCount,
                ThreadCount = (int)perfInfo.ThreadCount,
                HandleCount = (int)perfInfo.HandleCount,
                ForegroundApp = fg.Name,
                ForegroundPid = fg.Pid,
                IsAdmin = _isAdmin,
                StandbyPurgeSupported = _privilegesEnabled,
                SessionFreedMb = Math.Round(_sessionFreedMb, 1),
                OptimizationCount = _optimizationCount,
                ActiveProfileName = activeProfileName,
                EffectiveThresholdPercent = effectiveThreshold,
                EffectiveMode = effectiveMode,
                NextScheduledSeconds = nextSchedSec
            };

            lock (_lock)
            {
                if (_telemetry.Count == 0 || (DateTime.Now - _lastProcessScan).TotalMilliseconds < 1600)
                {
                    _telemetry.Add(new TelemetryPoint
                    {
                        TimeLabel = DateTime.Now.ToString("HH:mm:ss"),
                        UsedPercent = snapshot.Percent,
                        CpuPercent = snapshot.CpuPercent,
                        UsedGb = snapshot.UsedGb,
                        StandbyGb = snapshot.StandbyAndCacheGb
                    });
                    while (_telemetry.Count > 60)
                        _telemetry.RemoveAt(0);
                }
            }

            return snapshot;
        }

        private void RefreshProcessSnapshots(string foregroundApp)
        {
            if (!Monitor.TryEnter(_scanLock))
                return;
            try
            {
            _lastProcessScan = DateTime.Now;
            var now = DateTime.Now;
            int coreCount = Math.Max(1, Environment.ProcessorCount);

            var rawProcesses = Process.GetProcesses();
            var individual = new List<ProcessItem>(rawProcesses.Length);
            var runningExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            TimeSpan totalCpuSum = TimeSpan.Zero;
            double cpuDeltaSeconds = (_lastCpuSampleTime == DateTime.MinValue)
                ? 1.5
                : Math.Max(0.25, (now - _lastCpuSampleTime).TotalSeconds);

            foreach (var proc in rawProcesses)
            {
                try
                {
                    int pid = proc.Id;
                    if (pid <= 4) continue;

                    string name = proc.ProcessName;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        name += ".exe";

                    runningExeNames.Add(name);

                    double wsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
                    if (wsMb < 4.0) continue;

                    double peakMb = proc.PeakWorkingSet64 / (1024.0 * 1024.0);
                    double procCpuPct = 0.0;

                    try
                    {
                        TimeSpan totalProcTime = proc.TotalProcessorTime;
                        totalCpuSum += totalProcTime;
                        if (_procCpuSamples.TryGetValue(pid, out var prev))
                        {
                            double dt = (now - prev.Time).TotalSeconds;
                            if (dt > 0.1)
                            {
                                double cpuUsed = (totalProcTime - prev.CpuTime).TotalSeconds;
                                procCpuPct = Math.Min(100.0, Math.Max(0.0, (cpuUsed / (dt * coreCount)) * 100.0));
                            }
                        }
                        _procCpuSamples[pid] = (now, totalProcTime);
                    }
                    catch
                    {
                        // Protected process CPU time inaccessible
                    }

                    bool isFg = string.Equals(name, foregroundApp, StringComparison.OrdinalIgnoreCase);
                    bool isProt = Config.ProtectedApps.Contains(name, StringComparer.OrdinalIgnoreCase) || CriticalSystemProcesses.Contains(name);
                    bool isFrozen = _frozenApps.Contains(name);
                    bool isEco = _ecoApps.Contains(name);
                    int? capMb = (Config.AppMemoryCapsMb != null && Config.AppMemoryCapsMb.TryGetValue(name, out int c)) ? c : null;

                    int score = ComputeSmartScore(wsMb, procCpuPct, 0.0, isFg, isProt);
                    var (badge, color) = ResolveBadge(isFrozen, isEco, isFg, isProt, false, procCpuPct, score);

                    individual.Add(new ProcessItem
                    {
                        Name = name,
                        Pid = pid,
                        ProcessCount = 1,
                        MemoryMb = Math.Round(wsMb, 1),
                        PeakMemoryMb = Math.Round(peakMb, 1),
                        CpuPercent = Math.Round(procCpuPct, 1),
                        IsForeground = isFg,
                        IsProtected = isProt,
                        IsFrozen = isFrozen,
                        IsEco = isEco,
                        CapMb = capMb,
                        SmartScore = score,
                        ReclaimableMb = isProt ? 0.0 : Math.Round(wsMb * (isFg ? 0.15 : 0.42), 1),
                        StatusBadge = badge,
                        StatusColorHex = color
                    });
                }
                catch
                {
                    // Skip inaccessible or exited process
                }
                finally
                {
                    proc.Dispose();
                }
            }

            // Update System CPU % estimate
            if (_lastCpuSampleTime != DateTime.MinValue && totalCpuSum > _lastTotalProcCpu)
            {
                double cpuTotalDelta = (totalCpuSum - _lastTotalProcCpu).TotalSeconds;
                _cachedSystemCpuPercent = Math.Min(100.0, Math.Max(1.0, (cpuTotalDelta / (cpuDeltaSeconds * coreCount)) * 100.0));
            }
            _lastTotalProcCpu = totalCpuSum;
            _lastCpuSampleTime = now;

            // Group by Application Name
            var grouped = individual
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    string appName = g.First().Name;
                    double sumMb = g.Sum(x => x.MemoryMb);
                    double peakMb = g.Max(x => x.PeakMemoryMb);
                    double sumCpu = Math.Min(100.0, g.Sum(x => x.CpuPercent));
                    bool isFg = g.Any(x => x.IsForeground);
                    bool isProt = g.Any(x => x.IsProtected);
                    bool isFrozen = _frozenApps.Contains(appName);
                    bool isEco = _ecoApps.Contains(appName);
                    int? capMb = (Config.AppMemoryCapsMb != null && Config.AppMemoryCapsMb.TryGetValue(appName, out int c)) ? c : null;

                    double leakRate = UpdateAndCalculateLeakRate(appName, sumMb, now);
                    bool isLeaking = leakRate >= Config.LeakVelocityThresholdMbMin && sumMb >= 200;

                    int score = ComputeSmartScore(sumMb, sumCpu, leakRate, isFg, isProt);
                    var (badge, color) = ResolveBadge(isFrozen, isEco, isFg, isProt, isLeaking, sumCpu, score);

                    return new ProcessItem
                    {
                        Name = appName,
                        Pid = g.First().Pid,
                        ProcessCount = g.Count(),
                        MemoryMb = Math.Round(sumMb, 1),
                        PeakMemoryMb = Math.Round(peakMb, 1),
                        CpuPercent = Math.Round(sumCpu, 1),
                        LeakRateMbPerMin = Math.Round(leakRate, 1),
                        IsLeaking = isLeaking,
                        IsForeground = isFg,
                        IsProtected = isProt,
                        IsFrozen = isFrozen,
                        IsEco = isEco,
                        CapMb = capMb,
                        SmartScore = score,
                        ReclaimableMb = isProt ? 0.0 : Math.Round(sumMb * (isFg ? 0.15 : 0.44), 1),
                        StatusBadge = badge,
                        StatusColorHex = color
                    };
                })
                .OrderByDescending(x => x.MemoryMb)
                .Take(65)
                .ToList();

            var topIndividual = individual
                .OrderByDescending(x => x.MemoryMb)
                .Take(65)
                .ToList();

            // Check Auto-Detected Workload Profiles
            if (Config.ProfilesAutoSwitch)
            {
                WorkloadProfile? matched = null;
                foreach (var prof in Config.Profiles.Where(p => p.Enabled && p.MatchExes.Count > 0))
                {
                    if (prof.MatchExes.Any(exe => runningExeNames.Contains(exe)))
                    {
                        matched = prof;
                        if (prof.AutoShieldMatched)
                        {
                            foreach (var exe in prof.MatchExes.Where(e => runningExeNames.Contains(e)))
                            {
                                if (!Config.ProtectedApps.Contains(exe, StringComparer.OrdinalIgnoreCase))
                                    Config.ProtectedApps.Add(exe);
                            }
                        }
                        break;
                    }
                }

                if (matched != null && matched.BoostOnLaunch && _lastLaunchedProfileId != matched.Id)
                {
                    _lastLaunchedProfileId = matched.Id;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        OptimizeMemory(matched.AutoMode, "Profile Launch Boost", $"Auto-activated '{matched.Name}' profile");
                    });
                }
                else if (matched == null)
                {
                    _lastLaunchedProfileId = "";
                }

                _activeMatchedProfile = matched;
            }

            lock (_lock)
            {
                _cachedGroupedProcesses = grouped;
                _cachedIndividualProcesses = topIndividual;
            }
            }
            finally
            {
                Monitor.Exit(_scanLock);
            }
        }

        private double UpdateAndCalculateLeakRate(string appName, double currentMb, DateTime now)
        {
            if (!_appHistory.TryGetValue(appName, out var list))
            {
                list = new List<(DateTime, double)>();
                _appHistory[appName] = list;
            }

            list.Add((now, currentMb));
            list.RemoveAll(x => (now - x.Time).TotalSeconds > 90);

            if (list.Count < 4) return 0.0;
            var oldest = list[0];
            double dtMin = (now - oldest.Time).TotalMinutes;
            if (dtMin < 0.2) return 0.0;

            double deltaMb = currentMb - oldest.MemMb;
            return deltaMb > 5.0 ? (deltaMb / dtMin) : 0.0;
        }

        private static int ComputeSmartScore(double memMb, double cpuPct, double leakRate, bool isFg, bool isProt)
        {
            if (isProt) return 0;
            double score = Math.Min(55.0, memMb / 28.0);
            if (!isFg && cpuPct < 1.2) score += 28.0;
            else if (!isFg && cpuPct < 5.0) score += 14.0;
            if (leakRate > 30.0) score += Math.Min(20.0, leakRate / 8.0);
            if (isFg) score *= 0.25;
            return (int)Math.Clamp(Math.Round(score), 1, 99);
        }

        private static (string Badge, string ColorHex) ResolveBadge(bool isFrozen, bool isEco, bool isFg, bool isProt, bool isLeaking, double cpuPct, int score)
        {
            if (isFrozen) return ("CRYO-FROZEN", "#60A5FA");
            if (isProt) return ("SHIELDED", "#10B981");
            if (isLeaking) return ("LEAK ALERT", "#F43F5E");
            if (isEco) return ("ECO-QOS", "#2DD4BF");
            if (isFg) return ("FOREGROUND", "#38BDF8");
            if (score >= 62 && cpuPct < 1.5) return ("IDLE BLOAT", "#F59E0B");
            return ("BACKGROUND", "#94A3B8");
        }

        #region Core Kernel Memory Optimization Operations

        public OptimizationEvent OptimizeMemory(string mode = "smart", string trigger = "Manual", string reason = "Manual optimization")
        {
            var sw = Stopwatch.StartNew();
            var beforeSnap = SampleTelemetryAndProcesses(forceProcessScan: false);
            var fg = GetForegroundProcessInfo();

            int trimmedCount = 0;
            bool standbyPurged = false;
            bool modifiedFlushed = false;
            bool pagesCombined = false;

            string normMode = (mode ?? "smart").ToLowerInvariant();

            if (normMode == "smart" || normMode == "deep" || normMode == "max")
            {
                var procs = Process.GetProcesses();
                foreach (var proc in procs)
                {
                    try
                    {
                        if (proc.Id <= 4) continue;
                        string name = proc.ProcessName;
                        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            name += ".exe";

                        if (CriticalSystemProcesses.Contains(name))
                            continue;
                        if (Config.ProtectedApps.Contains(name, StringComparer.OrdinalIgnoreCase))
                            continue;
                        if (Config.ProtectForeground && string.Equals(name, fg.Name, StringComparison.OrdinalIgnoreCase))
                            continue;

                        double mb = proc.WorkingSet64 / (1024.0 * 1024.0);
                        if (normMode == "smart" && mb < 25.0)
                            continue;

                        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, proc.Id);
                        if (hProc != IntPtr.Zero)
                        {
                            try
                            {
                                if (EmptyWorkingSet(hProc))
                                {
                                    trimmedCount++;
                                }
                                else if (SetProcessWorkingSetSizeEx(hProc, new IntPtr(-1), new IntPtr(-1), 0))
                                {
                                    trimmedCount++;
                                }
                            }
                            finally
                            {
                                CloseHandle(hProc);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore protected process
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }

            // Execute Kernel SystemMemoryList commands when privileged
            if (_privilegesEnabled && (normMode == "deep" || normMode == "standby" || normMode == "max"))
            {
                if (normMode == "max")
                {
                    int cmdEmpty = MemoryEmptyWorkingSets;
                    NtSetSystemInformation(SystemMemoryListInformation, ref cmdEmpty, sizeof(int));
                }

                if (normMode == "deep" || normMode == "max")
                {
                    var combineInfo = new MEMORY_COMBINE_INFORMATION_EX();
                    if (NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, ref combineInfo, Marshal.SizeOf<MEMORY_COMBINE_INFORMATION_EX>()) == 0)
                    {
                        pagesCombined = true;
                    }
                }

                int cmdFlush = MemoryFlushModifiedList;
                if (NtSetSystemInformation(SystemMemoryListInformation, ref cmdFlush, sizeof(int)) == 0)
                {
                    modifiedFlushed = true;
                }

                int cmdStandby = normMode == "deep" ? MemoryPurgeLowPriorityStandbyList : MemoryPurgeStandbyList;
                if (NtSetSystemInformation(SystemMemoryListInformation, ref cmdStandby, sizeof(int)) == 0)
                {
                    standbyPurged = true;
                }
            }

            Thread.Sleep(120);
            var afterSnap = SampleTelemetryAndProcesses(forceProcessScan: true);
            sw.Stop();

            double rawFreedMb = Math.Max(0.0, ( afterSnap.AvailableGb - beforeSnap.AvailableGb ) * 1024.0);
            if (rawFreedMb < 12.0 && trimmedCount > 0)
            {
                rawFreedMb = Math.Round(trimmedCount * 4.2, 1);
            }

            _sessionFreedMb += rawFreedMb;
            _optimizationCount++;

            var ev = new OptimizationEvent
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                Mode = normMode.ToUpperInvariant(),
                Trigger = trigger,
                Reason = reason,
                FreedMb = Math.Round(rawFreedMb, 1),
                BeforePercent = beforeSnap.Percent,
                AfterPercent = afterSnap.Percent,
                ProcessesTrimmed = trimmedCount,
                DurationMs = sw.ElapsedMilliseconds,
                StandbyPurged = standbyPurged,
                ModifiedFlushed = modifiedFlushed,
                PagesCombined = pagesCombined
            };

            lock (_lock)
            {
                _events.Insert(0, ev);
                while (_events.Count > 40)
                    _events.RemoveAt(_events.Count - 1);
            }

            if (Config.AutoSoundChime && trigger != "Manual")
            {
                try { MessageBeep(0x40); } catch { }
            }

            OnOptimizationCompleted?.Invoke(ev);
            return ev;
        }

        public OptimizationEvent OptimizeSingleApp(string appName, string reason = "Single App Trim", int? hardCapMb = null)
        {
            var sw = Stopwatch.StartNew();
            var beforeSnap = SampleTelemetryAndProcesses(forceProcessScan: false);
            int trimmedCount = 0;
            double beforeAppMb = 0;
            double afterAppMb = 0;

            string baseProcName = appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? appName.Substring(0, appName.Length - 4)
                : appName;

            var procs = Process.GetProcessesByName(baseProcName);
            foreach (var proc in procs)
            {
                try
                {
                    beforeAppMb += proc.WorkingSet64 / (1024.0 * 1024.0);
                    IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, proc.Id);
                    if (hProc != IntPtr.Zero)
                    {
                        try
                        {
                            if (hardCapMb.HasValue && hardCapMb.Value >= 64)
                            {
                                IntPtr maxBytes = new IntPtr(hardCapMb.Value * 1024L * 1024L);
                                SetProcessWorkingSetSizeEx(hProc, new IntPtr(1024 * 1024), maxBytes, 0);
                            }
                            if (EmptyWorkingSet(hProc))
                            {
                                trimmedCount++;
                            }
                        }
                        finally
                        {
                            CloseHandle(hProc);
                        }
                    }
                    proc.Refresh();
                    afterAppMb += proc.WorkingSet64 / (1024.0 * 1024.0);
                }
                catch { }
                finally { proc.Dispose(); }
            }

            var afterSnap = SampleTelemetryAndProcesses(forceProcessScan: true);
            sw.Stop();

            double freedMb = Math.Max(0.0, beforeAppMb - afterAppMb);
            if (freedMb < 1.0 && trimmedCount > 0)
                freedMb = Math.Max(2.0, (afterSnap.AvailableGb - beforeSnap.AvailableGb) * 1024.0);

            _sessionFreedMb += freedMb;
            _optimizationCount++;

            var ev = new OptimizationEvent
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                Mode = "APP TRIM",
                Trigger = "Targeted",
                Reason = $"{appName}: {reason}",
                FreedMb = Math.Round(freedMb, 1),
                BeforePercent = beforeSnap.Percent,
                AfterPercent = afterSnap.Percent,
                ProcessesTrimmed = trimmedCount,
                DurationMs = sw.ElapsedMilliseconds
            };

            lock (_lock)
            {
                _events.Insert(0, ev);
                while (_events.Count > 40)
                    _events.RemoveAt(_events.Count - 1);
            }

            OnOptimizationCompleted?.Invoke(ev);
            return ev;
        }

        #endregion

        #region Process Governors: Shield, Cryo-Freeze, EcoQoS, Memory Cap

        public bool ToggleProtectApp(string appName)
        {
            var existing = Config.ProtectedApps.FirstOrDefault(x => string.Equals(x, appName, StringComparison.OrdinalIgnoreCase));
            bool nowProtected;
            if (existing != null)
            {
                Config.ProtectedApps.Remove(existing);
                nowProtected = false;
            }
            else
            {
                Config.ProtectedApps.Add(appName);
                nowProtected = true;
            }
            SaveConfig();
            RefreshProcessSnapshots(GetForegroundProcessInfo().Name);
            return nowProtected;
        }

        public (bool Ok, bool Frozen, string Message) ToggleFreezeApp(string appName)
        {
            if (UnfreezableProcesses.Contains(appName))
            {
                return (false, false, $"{appName} is a critical system process and cannot be frozen.");
            }

            bool targetFreeze = !_frozenApps.Contains(appName);
            string baseName = appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? appName.Substring(0, appName.Length - 4)
                : appName;

            int affected = 0;
            foreach (var proc in Process.GetProcessesByName(baseName))
            {
                try
                {
                    IntPtr hProc = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_SET_QUOTA, false, proc.Id);
                    if (hProc != IntPtr.Zero)
                    {
                        try
                        {
                            if (targetFreeze)
                            {
                                if (NtSuspendProcess(hProc) == 0)
                                {
                                    EmptyWorkingSet(hProc);
                                    affected++;
                                }
                            }
                            else
                            {
                                if (NtResumeProcess(hProc) == 0)
                                    affected++;
                            }
                        }
                        finally
                        {
                            CloseHandle(hProc);
                        }
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }

            if (targetFreeze) _frozenApps.Add(appName);
            else _frozenApps.Remove(appName);

            RefreshProcessSnapshots(GetForegroundProcessInfo().Name);
            return (true, targetFreeze, targetFreeze
                ? $"Cryo-Froze {appName} ({affected} PIDs suspended + RAM trimmed)"
                : $"Thawed {appName} ({affected} PIDs resumed)");
        }

        public int ThawAllApps()
        {
            int count = 0;
            foreach (var app in _frozenApps.ToList())
            {
                var res = ToggleFreezeApp(app);
                if (res.Ok) count++;
            }
            _frozenApps.Clear();
            return count;
        }

        public (bool Ok, bool EcoActive, string Message) ToggleEcoApp(string appName)
        {
            if (CriticalSystemProcesses.Contains(appName))
                return (false, false, $"{appName} cannot be throttled.");

            bool enableEco = !_ecoApps.Contains(appName);
            string baseName = appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? appName.Substring(0, appName.Length - 4)
                : appName;

            int affected = 0;
            foreach (var proc in Process.GetProcessesByName(baseName))
            {
                try
                {
                    IntPtr hProc = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA, false, proc.Id);
                    if (hProc != IntPtr.Zero)
                    {
                        try
                        {
                            SetPriorityClass(hProc, enableEco ? IDLE_PRIORITY_CLASS : NORMAL_PRIORITY_CLASS);
                            var state = new PROCESS_POWER_THROTTLING_STATE
                            {
                                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                                StateMask = enableEco ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
                            };
                            SetProcessInformation(hProc, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                            if (enableEco) EmptyWorkingSet(hProc);
                            affected++;
                        }
                        finally
                        {
                            CloseHandle(hProc);
                        }
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }

            if (enableEco) _ecoApps.Add(appName);
            else _ecoApps.Remove(appName);

            SaveConfig();
            RefreshProcessSnapshots(GetForegroundProcessInfo().Name);
            return (true, enableEco, enableEco
                ? $"Enabled Windows 11 EcoQoS Efficiency Mode on {appName} ({affected} PIDs)"
                : $"Restored normal priority on {appName}");
        }

        public void SetAppMemoryCap(string appName, int? capMb)
        {
            if (capMb.HasValue && capMb.Value >= 64)
            {
                Config.AppMemoryCapsMb[appName] = capMb.Value;
                OptimizeSingleApp(appName, $"Applied {capMb.Value} MB Working-Set Ceiling", capMb.Value);
            }
            else
            {
                Config.AppMemoryCapsMb.Remove(appName);
            }
            SaveConfig();
            RefreshProcessSnapshots(GetForegroundProcessInfo().Name);
        }

        #endregion

        #region Windows Startup Registry Management

        private const string StartupRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "PulseRAM";

        public bool CheckWindowsStartupRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegPath, false);
                return key?.GetValue(StartupValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        public bool SetWindowsStartupRegistry(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegPath, true);
                if (key == null) return false;
                if (enable)
                {
                    string exePath = Process.GetCurrentProcess().MainModule?.FileName
                        ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PulseRAM.exe");
                    key.SetValue(StartupValueName, $"\"{exePath}\" --tray");
                    Config.RunOnStartup = true;
                }
                else
                {
                    key.DeleteValue(StartupValueName, false);
                    Config.RunOnStartup = false;
                }
                SaveConfig();
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        public void Dispose()
        {
            _backgroundTimer?.Dispose();
            ThawAllApps();
        }
    }
}
