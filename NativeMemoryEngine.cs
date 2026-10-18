using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using PulseRAM.Core;

namespace PulseRAM
{
    public class MemorySnapshot
    {
        public double TotalGb { get; set; }
        public double UsedGb { get; set; }
        public double AvailableGb { get; set; }
        /// <summary>Standby list size (Core + Normal + Reserve priority). Measured via PDH when available.</summary>
        public double StandbyAndCacheGb { get; set; }
        /// <summary>Free + Zero page lists. Measured via PDH when available.</summary>
        public double FreeExactGb { get; set; }
        /// <summary>True when Standby/Free values come from the real memory-list counters, false when they are estimates.</summary>
        public bool MemoryListsMeasured { get; set; }
        /// <summary>Standby list per priority 0–7 in GB (null when the kernel memory-list query is unavailable).</summary>
        public double[]? StandbyByPriorityGb { get; set; }
        /// <summary>
        /// How fast Windows is evicting standby (cached) pages to satisfy new allocations, in MB/min.
        /// Negative when unknown. Sustained repurposing means real memory pressure.
        /// </summary>
        public double RepurposeMbPerMin { get; set; } = -1;
        /// <summary>True when purging the standby cache would actually help (free memory is low / cache is being evicted).</summary>
        public bool StandbyPurgeWorthwhile { get; set; }
        /// <summary>"ok", "high" (≥ 90% of the commit limit) or "critical" (≥ 96%), with hysteresis.</summary>
        public string CommitLevel { get; set; } = "ok";
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
        /// <summary>Current Auto-Pilot cooldown including any adaptive back-off.</summary>
        public int EffectiveCooldownSeconds { get; set; }
    }

    public class ProcessItem
    {
        public string Name { get; set; } = "";
        public int Pid { get; set; }
        public int ProcessCount { get; set; } = 1;
        /// <summary>Working set (physical RAM currently mapped) in MB.</summary>
        public double MemoryMb { get; set; }
        /// <summary>Private committed memory in MB (what actually grows when an app leaks).</summary>
        public double PrivateMb { get; set; }
        public double PeakMemoryMb { get; set; }
        public double CpuPercent { get; set; }
        /// <summary>Sustained private-memory growth rate in MB/min.</summary>
        public double LeakRateMbPerMin { get; set; }
        public bool IsLeaking { get; set; }
        public bool IsForeground { get; set; }
        public bool IsProtected { get; set; }
        public bool IsFrozen { get; set; }
        public bool IsEco { get; set; }
        public int? CapMb { get; set; }
        public int SmartScore { get; set; }
        /// <summary>Working set a trim could page out (0 when shielded). An upper bound, not a promise.</summary>
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
        /// <summary>
        /// Measured memory reclaimed: working-set MB actually trimmed, or for a pure standby purge,
        /// the measured growth of the free page list. Never an estimate.
        /// </summary>
        public double FreedMb { get; set; }
        /// <summary>Measured change in "Available" RAM (free + standby).</summary>
        public double AvailableGainMb { get; set; }
        /// <summary>Measured change in the free + zero page lists (0 if counters are unavailable).</summary>
        public double FreeListGainMb { get; set; }
        public double BeforePercent { get; set; }
        public double AfterPercent { get; set; }
        public int ProcessesTrimmed { get; set; }
        public long DurationMs { get; set; }
        public bool StandbyPurged { get; set; }
        public bool ModifiedFlushed { get; set; }
        public bool PagesCombined { get; set; }
        /// <summary>Memory saved by combining identical pages, in MB.</summary>
        public double CombinedMb { get; set; }
        /// <summary>True when nothing was done (e.g. a kernel-only mode without administrator rights).</summary>
        public bool Skipped { get; set; }
        /// <summary>Automatic purge deliberately not done because there was no memory pressure (not logged, no back-off).</summary>
        public bool SkippedNoPressure { get; set; }
        /// <summary>Extra human-readable detail, e.g. why the kernel purge was skipped.</summary>
        public string Note { get; set; } = "";
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
        public const int CurrentVersion = 3;
        public const string DefaultHotkeyPrimary = "Ctrl+Alt+Shift+M";
        public const string DefaultHotkeySecondary = "Ctrl+Alt+Shift+P";

        /// <summary>Absent in v1 files, so it defaults to 1 and triggers migration; saved files carry CurrentVersion.</summary>
        public int ConfigVersion { get; set; } = 1;
        public bool AutoEnabled { get; set; } = true;
        public int RamThresholdPercent { get; set; } = 80;
        public double MinFreeRamGb { get; set; } = 1.0;
        /// <summary>Base Auto-Pilot cooldown. Doubles automatically (up to 30 min) while sweeps fail to relieve pressure.</summary>
        public int CooldownSeconds { get; set; } = 120;
        public string AutoMode { get; set; } = "smart";
        public bool ProtectForeground { get; set; } = true;
        /// <summary>Smart mode only trims processes using less CPU than this (i.e. idle ones).</summary>
        public double IdleCpuCeilingPercent { get; set; } = 2.5;
        /// <summary>Smart mode ignores processes with a smaller working set than this.</summary>
        public double MinProcessWorkingSetMb { get; set; } = 15.0;
        /// <summary>Skip Auto-Pilot sweeps while total CPU is above CpuGuardPercent (trimming adds page faults to a busy system).</summary>
        public bool PauseOnHighCpu { get; set; } = true;
        public double CpuGuardPercent { get; set; } = 75.0;
        public bool AutoLeakRemediation { get; set; } = true;
        public double LeakVelocityThresholdMbMin { get; set; } = 120.0;
        public int LeakGuardCooldownSeconds { get; set; } = 300;
        /// <summary>Apps the user chose to stop hearing about in the leak list.</summary>
        public List<string> LeakIgnoredApps { get; set; } = new List<string>();
        public int CapGuardCooldownSeconds { get; set; } = 60;
        public bool ScheduledTrimEnabled { get; set; } = false;
        public int ScheduledIntervalMinutes { get; set; } = 15;
        public bool AutoSoundChime { get; set; } = false;
        public bool FocusBoostActive { get; set; } = false;
        public int FocusBoostThresholdPercent { get; set; } = 62;
        public bool RunOnStartup { get; set; } = false;
        public bool ProfilesAutoSwitch { get; set; } = true;
        public string ForcedProfileId { get; set; } = "";
        public bool HotkeysEnabled { get; set; } = true;
        public string HotkeyPrimary { get; set; } = DefaultHotkeyPrimary;
        public string HotkeySecondary { get; set; } = DefaultHotkeySecondary;
        public List<string> ProtectedApps { get; set; } = new List<string>();
        public List<string> EcoApps { get; set; } = new List<string>();
        public Dictionary<string, int> AppMemoryCapsMb { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public List<WorkloadProfile> Profiles { get; set; } = new List<WorkloadProfile>();
        /// <summary>"auto" (lite on low-end PCs), "lite" (no ambient animation, slower refresh) or "full".</summary>
        public string UiMode { get; set; } = "auto";
        /// <summary>
        /// Automatic sweeps only purge the standby cache when free memory is low or Windows is already evicting
        /// cache. Purging a healthy cache just makes apps reload from disk. Manual sweeps always purge.
        /// </summary>
        public bool SmartStandbyPurge { get; set; } = true;
        /// <summary>Draw the current RAM % into the tray icon.</summary>
        public bool TrayShowsPercent { get; set; } = true;
        /// <summary>Run PulseRAM itself in Windows Efficiency Mode (EcoQoS) while it is hidden in the tray.</summary>
        public bool EfficiencyModeWhenHidden { get; set; } = true;
        /// <summary>"system" (follow Windows), "dark" or "light".</summary>
        public string Theme { get; set; } = "system";
        /// <summary>Warn when committed memory nears the commit limit (RAM + page file) — the real cause of "out of memory".</summary>
        public bool CommitWarningEnabled { get; set; } = true;
        /// <summary>Last window placement in physical pixels (null = centre on the primary screen).</summary>
        public int? WindowX { get; set; }
        public int? WindowY { get; set; }
        public int? WindowWidth { get; set; }
        public int? WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
        /// <summary>Check GitHub Releases for a newer version, at most once a day. The only network request PulseRAM makes.</summary>
        public bool CheckForUpdates { get; set; } = true;
        public DateTime? LastUpdateCheckUtc { get; set; }
        /// <summary>A version the user chose to skip ("1.4.1"); not offered again.</summary>
        public string SkippedUpdateVersion { get; set; } = "";
        public bool WelcomeShown { get; set; }
        /// <summary>The window's minimize button hides PulseRAM to the tray instead of the taskbar.</summary>
        public bool MinimizeToTray { get; set; } = true;
        /// <summary>The one-time "still running in the tray" notice has been shown.</summary>
        public bool TrayHintShown { get; set; }
        /// <summary>Compact widget becomes see-through while the mouse isn't over it.</summary>
        public bool MiniHudSeeThrough { get; set; }
        /// <summary>Last position of the compact widget (null = top-right corner).</summary>
        public int? MiniHudX { get; set; }
        public int? MiniHudY { get; set; }
        /// <summary>Tier the defaults were tuned for on first run ("low", "mid", "high"; empty = not tuned).</summary>
        public string TunedForTier { get; set; } = "";
    }

    public enum DeviceTier { Low, Mid, High }

    /// <summary>What kind of PC we are on, used to pick sensible defaults and the UI weight.</summary>
    /// <summary>What to tell the user when committed memory nears the commit limit.</summary>
    public sealed class CommitAdvice
    {
        public string Level { get; init; } = "high";
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
        /// <summary>True when changing the page file is part of the fix (offer to open its settings).</summary>
        public bool SuggestPageFile { get; init; }
        /// <summary>Same level was already announced within 30 minutes: show the banner, skip the notification.</summary>
        public bool IsRepeat { get; set; }
    }

    public sealed class DeviceInfo
    {
        public double RamGb { get; init; }
        public int LogicalCores { get; init; }
        public DeviceTier Tier { get; init; }

        public string TierName => Tier switch { DeviceTier.Low => "low", DeviceTier.Mid => "mid", _ => "high" };
        public string Label => $"{Math.Round(RamGb):0} GB · {LogicalCores} threads · " +
            (Tier switch { DeviceTier.Low => "Low-end", DeviceTier.Mid => "Mid-range", _ => "High-end" });
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
        private static extern bool GetProcessWorkingSetSizeEx(IntPtr hProcess, out IntPtr lpMinimumWorkingSetSize, out IntPtr lpMaximumWorkingSetSize, out uint Flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, uint ProcessInformationSize);

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int SystemInformationClass, ref int SystemInfo, int SystemInfoLength);

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int SystemInformationClass, ref MEMORY_COMBINE_INFORMATION_EX SystemInfo, int SystemInfoLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_MEMORY_LIST_INFORMATION
        {
            public UIntPtr ZeroPageCount;
            public UIntPtr FreePageCount;
            public UIntPtr ModifiedPageCount;
            public UIntPtr ModifiedNoWritePageCount;
            public UIntPtr BadPageCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public UIntPtr[] PageCountByPriority;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public UIntPtr[] RepurposedPagesByPriority;
            public UIntPtr ModifiedPageCountPageFile;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int SystemInformationClass, ref SYSTEM_MEMORY_LIST_INFORMATION SystemInformation, int SystemInformationLength, out int ReturnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr ProcessHandle);

        [StructLayout(LayoutKind.Sequential)]
        private struct THREAD_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadInformation(IntPtr hThread, int ThreadInformationClass, ref THREAD_POWER_THROTTLING_STATE ThreadInformation, uint ThreadInformationSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

        /// <summary>
        /// While PulseRAM sits in Efficiency Mode, a sweep must still run at full speed — it is needed exactly
        /// when the PC is under load. Opts the current thread out of EcoQoS and raises it to the normal-class
        /// level for the duration of the sweep, then restores it.
        /// </summary>
        private readonly struct SweepBoost : IDisposable
        {
            private const int ThreadPowerThrottling = 3;
            private const int THREAD_PRIORITY_NORMAL = 0;
            private const int THREAD_PRIORITY_HIGHEST = 2; // below-normal class + highest ≈ normal-class normal

            public static SweepBoost Begin()
            {
                try
                {
                    var st = new THREAD_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 0 }; // high QoS
                    SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, ref st, (uint)Marshal.SizeOf<THREAD_POWER_THROTTLING_STATE>());
                    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
                }
                catch { }
                return new SweepBoost();
            }

            public void Dispose()
            {
                try
                {
                    var st = new THREAD_POWER_THROTTLING_STATE { Version = 1, ControlMask = 0, StateMask = 0 }; // back to default
                    SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, ref st, (uint)Marshal.SizeOf<THREAD_POWER_THROTTLING_STATE>());
                    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_NORMAL);
                }
                catch { }
            }
        }

        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr ProcessHandle);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
            public ulong Value => ((ulong)High << 32) | Low;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

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

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_SUSPEND_RESUME = 0x0800;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        private const int ERROR_NOT_ALL_ASSIGNED = 1300;

        // EmptyWorkingSet / SetProcessWorkingSetSizeEx require QUERY(_LIMITED) + SET_QUOTA.
        private const uint TRIM_ACCESS = PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA;

        private const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x00000002;
        private const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x00000004;
        private const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x00000008;

        private const uint IDLE_PRIORITY_CLASS = 0x00000040;
        private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        private const int ProcessPowerThrottling = 4;
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        private const int SystemMemoryListInformation = 80;
        private const int SystemCombinePhysicalMemoryInformation = 130;
        private const int MemoryFlushModifiedList = 3;
        private const int MemoryPurgeStandbyList = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 5;

        #endregion

        #region Memory-list performance counters (PDH)

        /// <summary>
        /// Reads the real Standby and Free/Zero page-list sizes from the built-in "Memory" performance
        /// counters. These are readable without administrator rights.
        /// </summary>
        private sealed class MemoryListCounters : IDisposable
        {
            [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
            private static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

            [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
            private static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

            [DllImport("pdh.dll")]
            private static extern uint PdhCollectQueryData(IntPtr hQuery);

            [DllImport("pdh.dll")]
            private static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, IntPtr lpdwType, out PDH_FMT_COUNTERVALUE pValue);

            [DllImport("pdh.dll")]
            private static extern uint PdhCloseQuery(IntPtr hQuery);

            [StructLayout(LayoutKind.Explicit)]
            private struct PDH_FMT_COUNTERVALUE
            {
                [FieldOffset(0)] public uint CStatus;
                [FieldOffset(8)] public long LargeValue;
            }

            private const uint PDH_FMT_LARGE = 0x00000400;

            private readonly object _gate = new object();
            private IntPtr _query = IntPtr.Zero;
            private readonly List<IntPtr> _standbyCounters = new List<IntPtr>();
            private IntPtr _freeCounter = IntPtr.Zero;

            public bool IsAvailable { get; }

            public MemoryListCounters()
            {
                try
                {
                    if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
                    foreach (var path in new[]
                    {
                        @"\Memory\Standby Cache Core Bytes",
                        @"\Memory\Standby Cache Normal Priority Bytes",
                        @"\Memory\Standby Cache Reserve Bytes"
                    })
                    {
                        if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out IntPtr c) == 0)
                            _standbyCounters.Add(c);
                    }
                    PdhAddEnglishCounterW(_query, @"\Memory\Free & Zero Page List Bytes", IntPtr.Zero, out _freeCounter);
                    IsAvailable = _standbyCounters.Count == 3 && _freeCounter != IntPtr.Zero && PdhCollectQueryData(_query) == 0;
                }
                catch
                {
                    IsAvailable = false;
                }
            }

            public bool TryRead(out double standbyBytes, out double freeBytes)
            {
                standbyBytes = 0;
                freeBytes = 0;
                if (!IsAvailable) return false;
                lock (_gate)
                {
                    try
                    {
                        if (PdhCollectQueryData(_query) != 0) return false;
                        foreach (var c in _standbyCounters)
                        {
                            if (PdhGetFormattedCounterValue(c, PDH_FMT_LARGE, IntPtr.Zero, out var v) != 0) return false;
                            standbyBytes += v.LargeValue;
                        }
                        if (PdhGetFormattedCounterValue(_freeCounter, PDH_FMT_LARGE, IntPtr.Zero, out var f) != 0) return false;
                        freeBytes = f.LargeValue;
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_query != IntPtr.Zero)
                    {
                        try { PdhCloseQuery(_query); } catch { }
                        _query = IntPtr.Zero;
                    }
                }
            }
        }

        #endregion

        #region Process name sets

        // Stored as BASE names (no ".exe"). Process.ProcessName never includes the extension and
        // pseudo-processes such as "Registry" and "Memory Compression" have none, so every check
        // goes through BaseName() to compare like with like.
        private static readonly HashSet<string> CriticalSystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "Secure System",
            "smss", "csrss", "wininit", "services", "lsass", "LsaIso", "lsm", "winlogon",
            "dwm", "fontdrvhost", "audiodg", "WUDFHost", "spoolsv", "MsMpEng",
            "PulseRAM"
        };

        private static readonly HashSet<string> UnfreezableProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "svchost", "taskmgr", "sihost", "ctfmon", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "RuntimeBroker", "TextInputHost",
            "conhost", "dllhost", "ApplicationFrameHost"
        };

        private static string BaseName(string name) =>
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;

        private static string ExeName(string processName) =>
            processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe";

        private static bool IsCritical(string name) => CriticalSystemProcesses.Contains(BaseName(name));

        private static readonly int OwnSessionId = Process.GetCurrentProcess().SessionId;

        /// <summary>
        /// Caps, freezing and Eco only ever touch processes in the user's own desktop session. Settings files can be
        /// edited by any program the user runs, so an elevated PulseRAM must not let them aim it at services or
        /// security software (session 0) or other users' sessions.
        /// </summary>
        private static bool InUserSession(Process p)
        {
            try
            {
                if (p.SessionId != OwnSessionId || p.SessionId == 0) return false;
                // Elevated PulseRAM: also leave elevated / SYSTEM processes on the desktop alone (installers, admin
                // tools, vendor agents). Only medium-or-lower integrity apps, i.e. ordinary user apps, qualify.
                if (InstallSecurity.IsElevated) return IntegrityLevel(p.Id) is int il && il < 0x3000;
                return true;
            }
            catch { return false; }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        /// <summary>Mandatory integrity level RID of a process (0x2000 medium, 0x3000 high, 0x4000 system), or null.</summary>
        private static int? IntegrityLevel(int pid)
        {
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return null;
            try
            {
                if (!OpenProcessToken(hProc, TOKEN_QUERY, out IntPtr hTok)) return null;
                try
                {
                    GetTokenInformation(hTok, 25 /* TokenIntegrityLevel */, IntPtr.Zero, 0, out int len);
                    if (len <= 0) return null;
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        if (!GetTokenInformation(hTok, 25, buf, len, out _)) return null;
                        IntPtr sid = Marshal.ReadIntPtr(buf); // TOKEN_MANDATORY_LABEL.Label.Sid
                        int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                        if (count == 0) return null;
                        return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(hTok); }
            }
            finally { CloseHandle(hProc); }
        }

        private static bool IsUnfreezable(string name) => IsCritical(name) || UnfreezableProcesses.Contains(BaseName(name));

        #endregion

        #region Frozen-process journal

        private sealed class FrozenRecord
        {
            public string Name { get; set; } = "";
            public int Pid { get; set; }
            public long StartTicksUtc { get; set; }
        }

        /// <summary>
        /// Portable mode: a file named portable.txt next to PulseRAM.exe keeps all settings, logs and the frozen journal
        /// in a "data" folder beside the EXE instead of %LocalAppData%\PulseRAM.
        /// </summary>
        public static bool IsPortable { get; } = DetectPortable();

        private static bool DetectPortable()
        {
            try
            {
                if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"))) return false;
                // Only if the folder is writable (e.g. not unpacked into Program Files); otherwise use %LocalAppData%.
                string dir = Path.Combine(AppContext.BaseDirectory, "data");
                Directory.CreateDirectory(dir);
                // CreateNew never follows an existing file or link; DeleteOnClose removes our own probe.
                string probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch
            {
                PortableUnavailable = true;
                return false;
            }
        }

        /// <summary>portable.txt is present but its folder can't be written, so settings went to %LocalAppData%.</summary>
        public static bool PortableUnavailable { get; private set; }

        public static string DataDirectory => IsPortable
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseRAM");

        private static string FrozenJournalPath => Path.Combine(DataDirectory, "frozen_processes.json");

        private static List<FrozenRecord> ReadFrozenJournal()
        {
            try
            {
                if (!File.Exists(FrozenJournalPath)) return new List<FrozenRecord>();
                return JsonSerializer.Deserialize<List<FrozenRecord>>(File.ReadAllText(FrozenJournalPath)) ?? new List<FrozenRecord>();
            }
            catch
            {
                return new List<FrozenRecord>();
            }
        }

        private static bool WriteFrozenJournal(List<FrozenRecord> records)
        {
            try
            {
                if (records.Count == 0) return SafeFile.Delete(FrozenJournalPath);
                Directory.CreateDirectory(DataDirectory);
                return SafeFile.WriteAllText(FrozenJournalPath, JsonSerializer.Serialize(records));
            }
            catch { return false; }
        }

        private static long TryGetStartTicks(Process proc)
        {
            try { return proc.StartTime.ToUniversalTime().Ticks; } catch { return 0; }
        }

        /// <summary>
        /// Resumes every process a previous PulseRAM session suspended but never thawed (crash, kill,
        /// upgrade or uninstall). Matches PID, name and start time so an unrelated process that reused
        /// the PID is never touched. Safe to call when nothing is frozen.
        /// </summary>
        public static int RecoverFrozenProcesses()
        {
            var records = ReadFrozenJournal();
            if (records.Count == 0) return 0;

            int resumed = 0;
            // Records for processes that still exist but could not be resumed (e.g. an elevated app frozen
            // by an elevated PulseRAM, and we are now running as a standard user) are KEPT so a later
            // elevated run can still resume them.
            var stillSuspended = new List<FrozenRecord>();
            foreach (var rec in records)
            {
                switch (TryResumeRecord(rec))
                {
                    case ResumeResult.Resumed: resumed++; break;
                    case ResumeResult.Failed: stillSuspended.Add(rec); break;
                    case ResumeResult.Gone: break;
                }
            }
            WriteFrozenJournal(stillSuspended);
            return resumed;
        }

        #endregion

        private readonly string _configPath;
        private readonly string _seedConfigPath;
        private readonly string _legacyConfigPath;

        private readonly object _lock = new object();          // events, telemetry, cached lists
        private readonly object _stateLock = new object();     // config collections, frozen/eco/shield sets
        private readonly object _scanLock = new object();      // process scan state
        private readonly object _optimizeLock = new object();  // serialises trims so manual + auto never overlap
        private readonly object _freezeLock = new object();    // serialises freeze/thaw decisions

        private readonly List<OptimizationEvent> _events = new List<OptimizationEvent>();
        private readonly List<TelemetryPoint> _telemetry = new List<TelemetryPoint>();
        private readonly Dictionary<string, List<(DateTime Time, double PrivateMb)>> _appHistory = new Dictionary<string, List<(DateTime, double)>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _frozenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FrozenRecord> _frozenRecords = new List<FrozenRecord>();
        private readonly HashSet<string> _ecoApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _profileShields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastCapAction = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastLeakAction = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly LeakHistory _leakHistory = new LeakHistory();

        /// <summary>Raised (on a background thread) the first time an app is seen leaking, then at most hourly per app.</summary>
        public event Action<LeakIncident>? OnLeakDetected;

        private readonly Dictionary<int, (DateTime Time, TimeSpan CpuTime)> _procCpuSamples = new Dictionary<int, (DateTime, TimeSpan)>();
        private volatile Dictionary<int, double> _lastPidCpu = new Dictionary<int, double>();
        private TimeSpan _lastTotalProcCpu = TimeSpan.Zero;
        private DateTime _lastCpuSampleTime = DateTime.MinValue;
        private double _cachedSystemCpuPercent = 0.0;
        private ulong _lastSysIdle, _lastSysKernel, _lastSysUser;
        private ulong _lastRepurposedPages;
        private DateTime _lastRepurposeSample = DateTime.MinValue;
        private double _repurposeMbPerMin = -1;
        private bool _memListQueryFailed;
        private readonly object _repurposeLock = new object();
        private DateTime _memListRetryAt = DateTime.MinValue;

        private List<ProcessItem> _cachedGroupedProcesses = new List<ProcessItem>();
        private List<ProcessItem> _cachedIndividualProcesses = new List<ProcessItem>();
        private DateTime _lastProcessScan = DateTime.MinValue;
        private volatile MemorySnapshot _lastSnapshot = new MemorySnapshot();

        private DateTime _lastAutoOptimizeTime = DateTime.MinValue;
        private int _autoBackoffLevel = 0;
        private DateTime _lastScheduledSweepTime = DateTime.Now;
        private double _sessionFreedMb = 0.0;
        private int _optimizationCount = 0;
        private bool _isAdmin = false;
        private bool _privilegesEnabled = false;
        private WorkloadProfile? _activeMatchedProfile = null;
        private string _lastLaunchedProfileId = "";

        private readonly MemoryListCounters _memCounters;
        private Timer? _backgroundTimer;
        private int _tickRunning = 0;
        private bool _disposed = false;

        public EngineConfig Config { get; private set; } = new EngineConfig();

        public event Action<OptimizationEvent>? OnOptimizationCompleted;

        /// <summary>Raised (background thread) when commit charge climbs into "high" or "critical".</summary>
        public event Action<CommitAdvice>? OnCommitWarning;

        private string _commitLevel = "ok";
        private DateTime _lastCommitWarning = DateTime.MinValue;

        public NativeMemoryEngine()
        {
            Directory.CreateDirectory(DataDirectory);
            // User settings live in %LocalAppData%\PulseRAM so they always save, even for an all-users install
            // in Program Files.
            _configPath = Path.Combine(DataDirectory, "pulseram_config.json");
            // Defaults shipped next to the EXE (always overwritten by the installer).
            _seedConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pulseram_defaults.json");
            // v1.0 kept user settings next to the EXE; imported once if present.
            _legacyConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pulseram_config.json");

            // Enable SeDebugPrivilege first so recovery can also resume elevated processes when we're admin.
            CheckAdminAndEnablePrivileges();

            // Resume anything a previous session left suspended before we do anything else.
            RecoverFrozenProcesses();
            // Anything we could not resume stays journaled and shows as frozen so the user can thaw it
            // (e.g. after re-launching as administrator).
            foreach (var rec in ReadFrozenJournal())
            {
                _frozenRecords.Add(rec);
                _frozenApps.Add(ExeName(rec.Name));
            }

            Device = DetectDevice();
            LoadConfig();
            Log.Info($"PulseRAM {typeof(NativeMemoryEngine).Assembly.GetName().Version} on {Environment.OSVersion.VersionString} · " +
                     $"{Device.Label} · admin={_isAdmin} · purgeRights={_privilegesEnabled} · ui={Config.UiMode}");

            _memCounters = new MemoryListCounters();

            _lastSnapshot = SampleTelemetryAndProcesses(forceProcessScan: true, recordTelemetry: true);

            _backgroundTimer = new Timer(BackgroundTick, null, 1500, 1500);
        }

        public bool IsAdmin => _isAdmin;

        private void CheckAdminAndEnablePrivileges()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                _isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

                if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr hToken))
                {
                    // SeProfileSingleProcessPrivilege is what NtSetSystemInformation(SystemMemoryListInformation)
                    // and page combining actually require, so it alone decides whether kernel purges are possible.
                    bool profile = EnablePrivilege(hToken, "SeProfileSingleProcessPrivilege");
                    EnablePrivilege(hToken, "SeIncreaseQuotaPrivilege");
                    EnablePrivilege(hToken, "SeDebugPrivilege");
                    _privilegesEnabled = _isAdmin && profile;
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

            // AdjustTokenPrivileges returns TRUE even when the token does not hold the privilege;
            // ERROR_NOT_ALL_ASSIGNED is the only signal that it was not actually enabled.
            if (!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;
            return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
        }

        #region Configuration

        /// <summary>True until the welcome card has been shown once on this install (upgrades from 1.3 count as shown).</summary>
        public bool IsFirstRun { get; private set; }

        public void LoadConfig()
        {
            bool loadedFromSeed = false;
            bool freshDefaults = false;
            EngineConfig? loaded = null;
            try
            {
                string? path = null;
                if (File.Exists(_configPath)) path = _configPath;
                else if (IsEngineConfigFile(_legacyConfigPath)) path = _legacyConfigPath;
                else if (File.Exists(_seedConfigPath)) path = _seedConfigPath;

                freshDefaults = path == null || path == _seedConfigPath;
                if (path != null)
                {
                    loadedFromSeed = path != _configPath;
                    try
                    {
                        loaded = JsonSerializer.Deserialize<EngineConfig>(File.ReadAllText(path), JsonReadOptions);
                    }
                    catch when (path == _configPath)
                    {
                        // Keep a copy of an unreadable user config before defaults overwrite it.
                        try { SafeFile.WriteAllText(Path.Combine(DataDirectory, $"pulseram_config.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json"), File.ReadAllText(_configPath)); } catch { }
                        loaded = null;
                    }
                }
            }
            catch
            {
                loaded = null;
            }

            var cfg = loaded ?? new EngineConfig();

            cfg.Profiles ??= new List<WorkloadProfile>();
            if (cfg.Profiles.Count == 0) cfg.Profiles = GetDefaultProfiles();
            foreach (var p in cfg.Profiles) p.MatchExes ??= new List<string>();

            cfg.ProtectedApps ??= new List<string>();
            if (cfg.ProtectedApps.Count == 0)
                cfg.ProtectedApps = new List<string> { "explorer.exe", "dwm.exe", "audiodg.exe" };

            cfg.EcoApps ??= new List<string>();
            cfg.LeakIgnoredApps ??= new List<string>();
            IsFirstRun = !cfg.WelcomeShown; // re-evaluated after migration below
            // JSON deserialisation creates a case-sensitive dictionary; process names are case-insensitive.
            cfg.AppMemoryCapsMb = new Dictionary<string, int>(cfg.AppMemoryCapsMb ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);

            bool needsSave = loaded == null || loadedFromSeed || cfg.ConfigVersion < EngineConfig.CurrentVersion;
            MigrateConfig(cfg, existingUserSettings: !freshDefaults && loaded != null);
            if (freshDefaults || loaded == null) ApplyDeviceTuning(cfg, Device);
            IsFirstRun = !cfg.WelcomeShown;
            if (cfg.UiMode is not ("auto" or "lite" or "full")) cfg.UiMode = "auto";
            ClampConfig(cfg);

            lock (_stateLock)
            {
                Config = cfg;
                _ecoApps.Clear();
                foreach (var app in cfg.EcoApps) _ecoApps.Add(app);
            }

            AuditElevatedStartupTask();
            bool hasElevatedTask = ElevatedStartupTaskExists();
            bool hasRunKey = CheckWindowsStartupRegistry();
            if (hasElevatedTask && hasRunKey)
            {
                // e.g. an upgrade re-added the installer's Run-key entry: a non-elevated copy started from it
                // could win the single-instance race against the elevated task, so drop it.
                try { SetRunKey(false, ""); hasRunKey = false; } catch { }
            }
            Config.RunOnStartup = hasElevatedTask || hasRunKey;

            if (needsSave)
            {
                Config.ConfigVersion = EngineConfig.CurrentVersion;
                SaveConfig();
            }
        }

        private static readonly JsonSerializerOptions JsonReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>
        /// True only for a file written in EngineConfig's own schema. v1.0 also shipped a snake_case file
        /// with the same name whose keys the engine never understood; that one is ignored.
        /// </summary>
        private static bool IsEngineConfigFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                return doc.RootElement.EnumerateObject().Any(p =>
                    p.Name.Equals(nameof(EngineConfig.RamThresholdPercent), StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Equals(nameof(EngineConfig.AutoEnabled), StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static void MigrateConfig(EngineConfig cfg, bool existingUserSettings)
        {
            if (cfg.ConfigVersion >= EngineConfig.CurrentVersion) return;

            // v3 (PulseRAM 1.4): the welcome card is for new installs only, not for people upgrading.
            if (cfg.ConfigVersion < 3 && existingUserSettings) cfg.WelcomeShown = true;

            if (cfg.ConfigVersion >= 2)
            {
                cfg.ConfigVersion = EngineConfig.CurrentVersion;
                return;
            }

            // v1 shipped aggressive defaults that trimmed every 30 s on low-RAM PCs.
            if (cfg.CooldownSeconds < 120) cfg.CooldownSeconds = 120;
            if (Math.Abs(cfg.MinFreeRamGb - 2.0) < 0.01) cfg.MinFreeRamGb = 1.0;

            // v1 hard-coded Ctrl+Shift+M / Ctrl+Shift+P, which collide with Teams (mute) and VS Code (command palette).
            if (string.IsNullOrWhiteSpace(cfg.HotkeyPrimary) || cfg.HotkeyPrimary.Replace(" ", "").Equals("Ctrl+Shift+M", StringComparison.OrdinalIgnoreCase))
                cfg.HotkeyPrimary = EngineConfig.DefaultHotkeyPrimary;
            if (string.IsNullOrWhiteSpace(cfg.HotkeySecondary) || cfg.HotkeySecondary.Replace(" ", "").Equals("Ctrl+Shift+P", StringComparison.OrdinalIgnoreCase))
                cfg.HotkeySecondary = EngineConfig.DefaultHotkeySecondary;

            // v1 permanently copied auto-shielded profile EXEs (games, editors…) into ProtectedApps.
            // Shields are now temporary while a profile is active, so remove those leftovers.
            var profileExes = new HashSet<string>(
                cfg.Profiles.Where(p => p.AutoShieldMatched).SelectMany(p => p.MatchExes),
                StringComparer.OrdinalIgnoreCase);
            cfg.ProtectedApps.RemoveAll(app => profileExes.Contains(app));

            cfg.ConfigVersion = EngineConfig.CurrentVersion;
        }

        private static void ClampConfig(EngineConfig cfg)
        {
            cfg.RamThresholdPercent = Math.Clamp(cfg.RamThresholdPercent, 50, 95);
            cfg.MinFreeRamGb = Math.Clamp(cfg.MinFreeRamGb, 0.5, 8.0);
            cfg.CooldownSeconds = Math.Clamp(cfg.CooldownSeconds, 30, 3600);
            cfg.IdleCpuCeilingPercent = Math.Clamp(cfg.IdleCpuCeilingPercent, 0.1, 50.0);
            cfg.MinProcessWorkingSetMb = Math.Clamp(cfg.MinProcessWorkingSetMb, 0.0, 4096.0);
            cfg.CpuGuardPercent = Math.Clamp(cfg.CpuGuardPercent, 30.0, 100.0);
            cfg.LeakVelocityThresholdMbMin = Math.Clamp(cfg.LeakVelocityThresholdMbMin, 10.0, 10000.0);
            cfg.LeakGuardCooldownSeconds = Math.Clamp(cfg.LeakGuardCooldownSeconds, 60, 3600);
            cfg.CapGuardCooldownSeconds = Math.Clamp(cfg.CapGuardCooldownSeconds, 15, 3600);
            cfg.ScheduledIntervalMinutes = Math.Clamp(cfg.ScheduledIntervalMinutes, 5, 1440);
            cfg.FocusBoostThresholdPercent = Math.Clamp(cfg.FocusBoostThresholdPercent, 50, 95);
            if (!IsKnownMode(cfg.AutoMode)) cfg.AutoMode = "smart";
            foreach (var p in cfg.Profiles)
            {
                p.RamThresholdPercent = Math.Clamp(p.RamThresholdPercent, 50, 95);
                if (!IsKnownMode(p.AutoMode)) p.AutoMode = "smart";
            }
        }

        private static bool IsKnownMode(string? mode) =>
            mode is "smart" or "deep" or "standby" or "max";

        /// <summary>
        /// Low-end: under ~7 GB RAM or ≤ 2 threads. Mid-range: under ~15 GB (typically 8 GB) or ≤ 4 threads.
        /// High-end: everything else (16 GB+).
        /// </summary>
        public static DeviceInfo DetectDevice()
        {
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            double ramGb = GlobalMemoryStatusEx(ref ms) ? ms.ullTotalPhys / (1024.0 * 1024.0 * 1024.0) : 8.0;
            int cores = Math.Max(1, Environment.ProcessorCount);
            DeviceTier tier = MemoryPolicy.ClassifyTier(ramGb, cores) switch
            {
                Tier.Low => DeviceTier.Low,
                Tier.Mid => DeviceTier.Mid,
                _ => DeviceTier.High
            };
            return new DeviceInfo { RamGb = ramGb, LogicalCores = cores, Tier = tier };
        }

        /// <summary>
        /// Picks thresholds that suit the hardware. Low-RAM PCs normally sit at a high memory load, so they get
        /// a higher trigger, a smaller floor and a longer cooldown (otherwise Auto-Pilot would trim constantly and
        /// make the PC slower). Big-RAM PCs can afford a larger free-memory floor. Profile triggers are raised
        /// by the same margin so an always-matching profile (e.g. a browser) can't undo this.
        /// </summary>
        public static void ApplyDeviceTuning(EngineConfig cfg, DeviceInfo device)
        {
            var tier = device.Tier switch { DeviceTier.Low => Tier.Low, DeviceTier.Mid => Tier.Mid, _ => Tier.High };
            var t = MemoryPolicy.TuningFor(tier);
            cfg.RamThresholdPercent = t.TriggerPercent;
            cfg.MinFreeRamGb = t.MinFreeGb;
            cfg.CooldownSeconds = t.CooldownSeconds;
            cfg.MinProcessWorkingSetMb = t.MinProcessWorkingSetMb;
            foreach (var p in cfg.Profiles)
                p.RamThresholdPercent = MemoryPolicy.BumpProfileTrigger(p.RamThresholdPercent, tier);
            cfg.TunedForTier = device.TierName;
        }

        /// <summary>Re-applies the hardware-based defaults (thresholds, floor, cooldown, profile triggers).</summary>
        public void RetuneForThisDevice()
        {
            lock (_stateLock)
            {
                var fresh = GetDefaultProfiles();
                // Keep the user's enabled/disabled choice and custom EXE lists; reset only the triggers.
                foreach (var p in Config.Profiles)
                {
                    var d = fresh.FirstOrDefault(f => f.Id == p.Id);
                    if (d != null) p.RamThresholdPercent = d.RamThresholdPercent;
                }
                ApplyDeviceTuning(Config, Device);
            }
            _autoBackoffLevel = 0;
            SaveConfig();
        }

        public DeviceInfo Device { get; }

        /// <summary>Lightweight mode: explicit "lite", or "auto" on a low-end PC.</summary>
        public bool LiteMode => Config.UiMode == "lite" || (Config.UiMode != "full" && Device.Tier == DeviceTier.Low);

        /// <summary>Process scans are the most expensive thing we do; halve their rate in lite mode.</summary>
        private int ScanIntervalMs => LiteMode ? 4400 : 2200;

        private readonly object _saveLock = new object();

        public void SaveConfig()
        {
            try
            {
                string json;
                lock (_stateLock)
                {
                    Config.EcoApps = _ecoApps.ToList();
                    json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
                }
                // Saves come from the UI thread and engine threads; serialise the write + rename.
                lock (_saveLock)
                {
                    Directory.CreateDirectory(DataDirectory);
                    if (InstallSecurity.IsElevated)
                    {
                        // Elevated: write in place through a verified handle (a temp file + rename could be
                        // redirected by a link planted in the user-writable folder).
                        if (!SafeFile.WriteAllText(_configPath, json))
                            Log.Warn($"Not saving settings: {_configPath} is a link or redirected");
                        return;
                    }
                    string tmp = _configPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, _configPath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not save settings to {_configPath}", ex);
            }
        }

        /// <summary>Built-in profiles; kept identical to the Profiles in pulseram_defaults.json.</summary>
        private static List<WorkloadProfile> GetDefaultProfiles()
        {
            return new List<WorkloadProfile>
            {
                new WorkloadProfile
                {
                    Id = "gaming-boost",
                    Name = "Gaming Boost",
                    Description = "Trims idle background apps on game launch, shields the game process, and sweeps at 68%.",
                    MatchExes = new List<string> { "cs2.exe", "valorant.exe", "valorant-win64-shipping.exe", "fortniteclient-win64-shipping.exe", "eldenring.exe", "genshinimpact.exe", "starrail.exe", "robloxplayerbeta.exe", "dota2.exe", "league of legends.exe", "r5apex.exe", "cyberpunk2077.exe", "helldivers2.exe", "overwatch.exe", "javaw.exe", "minecraft.windows.exe" },
                    RamThresholdPercent = 68,
                    AutoMode = "smart",
                    AutoShieldMatched = true,
                    BoostOnLaunch = true,
                    Enabled = true
                },
                new WorkloadProfile
                {
                    Id = "creator-studio",
                    Name = "Creator & 3D Studio",
                    Description = "Shields active rendering/editing apps while trimming idle background apps at 72%.",
                    MatchExes = new List<string> { "blender.exe", "afterfx.exe", "adobe premiere pro.exe", "photoshop.exe", "resolve.exe", "unrealeditor.exe", "unity.exe", "obs64.exe" },
                    RamThresholdPercent = 72,
                    AutoMode = "smart",
                    AutoShieldMatched = true,
                    BoostOnLaunch = true,
                    Enabled = true
                },
                new WorkloadProfile
                {
                    Id = "dev-workstation",
                    Name = "Dev & Local AI",
                    Description = "Shields your IDE and local LLM runners while trimming idle helpers at 76%.",
                    MatchExes = new List<string> { "code.exe", "devenv.exe", "idea64.exe", "pycharm64.exe", "ollama.exe", "lm-studio.exe", "docker.exe" },
                    RamThresholdPercent = 76,
                    AutoMode = "smart",
                    AutoShieldMatched = true,
                    BoostOnLaunch = false,
                    Enabled = true
                },
                new WorkloadProfile
                {
                    Id = "browser-tamer",
                    Name = "Browser Tab Tamer",
                    Description = "Trims idle browser subprocesses when total memory passes 78%.",
                    MatchExes = new List<string> { "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe" },
                    RamThresholdPercent = 78,
                    AutoMode = "smart",
                    AutoShieldMatched = false,
                    BoostOnLaunch = false,
                    Enabled = true
                }
            };
        }

        #endregion

        #region Background loop: Auto-Pilot, schedule, caps, leak guard

        private void BackgroundTick(object? state)
        {
            // System.Threading.Timer does not wait for the previous callback; never run two ticks at once.
            if (Interlocked.Exchange(ref _tickRunning, 1) == 1) return;
            try
            {
                if (_disposed) return;
                var snap = SampleTelemetryAndProcesses(forceProcessScan: false, recordTelemetry: true);
                CheckCommitPressure(snap);

                // 1. Scheduled maintenance sweep
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

                // 2. Auto-Pilot thresholds with adaptive back-off
                if (Config.AutoEnabled)
                {
                    int effectiveThreshold = snap.EffectiveThresholdPercent;
                    bool overPercent = snap.Percent >= effectiveThreshold;
                    bool underFreeFloor = snap.AvailableGb <= Config.MinFreeRamGb;

                    if (!overPercent && !underFreeFloor)
                    {
                        // Pressure has cleared with some margin: reset the back-off.
                        if (snap.Percent < effectiveThreshold - 5 && snap.AvailableGb > Config.MinFreeRamGb + 0.5)
                            _autoBackoffLevel = 0;
                    }
                    else if ((DateTime.Now - _lastAutoOptimizeTime).TotalSeconds >= EffectiveCooldownSeconds())
                    {
                        bool cpuBusy = Config.PauseOnHighCpu && snap.CpuPercent >= Config.CpuGuardPercent;
                        if (!cpuBusy)
                        {
                            _lastAutoOptimizeTime = DateTime.Now;
                            string reason = overPercent
                                ? $"RAM reached {snap.Percent:F1}% (Threshold: {effectiveThreshold}%)"
                                : $"Available RAM dropped to {snap.AvailableGb:F2} GB (Floor: {Config.MinFreeRamGb:F1} GB)";
                            var ev = OptimizeMemory(snap.EffectiveMode, "Auto-Pilot", reason);

                            // If the sweep did not get us back under the trigger, the memory is genuinely in use:
                            // trimming again soon would only cause page-fault churn, so back off exponentially.
                            var after = _lastSnapshot;
                            bool stillPressured = after.Percent >= effectiveThreshold || after.AvailableGb <= Config.MinFreeRamGb;
                            // A deliberate no-pressure skip is temporary: never back off for it, or a later real
                            // shortage could wait up to 30 minutes.
                            if (!ev.SkippedNoPressure && (stillPressured || ev.Skipped))
                                _autoBackoffLevel = Math.Min(_autoBackoffLevel + 1, MemoryPolicy.MaxBackoffLevel);
                            return;
                        }
                    }
                }

                // 3. Per-app memory caps & runaway leak guard
                EnforceAppCapsAndLeakGuard(snap.ForegroundApp);
            }
            catch (Exception ex)
            {
                // Never crash the background loop, but record why it hiccupped.
                Log.Error("Background tick failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _tickRunning, 0);
            }
        }

        private int EffectiveCooldownSeconds()
        {
            return MemoryPolicy.BackoffCooldownSeconds(Config.CooldownSeconds, _autoBackoffLevel);
        }

        private void EnforceAppCapsAndLeakGuard(string foregroundApp)
        {
            List<ProcessItem> grouped;
            lock (_lock)
            {
                grouped = _cachedGroupedProcesses.ToList();
            }

            Dictionary<string, int> caps;
            lock (_stateLock)
            {
                caps = new Dictionary<string, int>(Config.AppMemoryCapsMb, StringComparer.OrdinalIgnoreCase);
            }

            var now = DateTime.Now;

            // Per-process hard caps: Windows enforces the limit on processes that already have it, so this only
            // has to catch NEW processes of a capped app. Checked at most once per CapGuardCooldownSeconds.
            foreach (var cap in caps)
            {
                if (cap.Value < 64 || IsCritical(cap.Key)) continue;
                bool due;
                lock (_stateLock)
                {
                    due = !_lastCapAction.TryGetValue(cap.Key, out var last) || (now - last).TotalSeconds >= Config.CapGuardCooldownSeconds;
                    if (due) _lastCapAction[cap.Key] = now;
                }
                if (due) EnforceHardCap(cap.Key, cap.Value, force: false);
            }

            foreach (var item in grouped)
            {
                if (IsCritical(item.Name))
                    continue;

                // Leak history: remember every app that keeps growing, including the one in the foreground
                // (the guard below never touches that one).
                if (item.IsLeaking && item.PrivateMb >= 300)
                {
                    bool notify;
                    LeakIncident? incident = null;
                    lock (_stateLock)
                    {
                        notify = _leakHistory.Record(item.Name, item.LeakRateMbPerMin, item.PrivateMb, now,
                            new HashSet<string>(Config.LeakIgnoredApps, StringComparer.OrdinalIgnoreCase));
                        if (notify) incident = _leakHistory.Snapshot().FirstOrDefault(i => string.Equals(i.App, item.Name, StringComparison.OrdinalIgnoreCase));
                    }
                    if (notify && incident != null)
                    {
                        Log.Warn($"Possible leak: {item.Name} private memory +{item.LeakRateMbPerMin:F0} MB/min ({item.PrivateMb:F0} MB)");
                        try { OnLeakDetected?.Invoke(incident); } catch (Exception ex) { Log.Error("Leak notification failed", ex); }
                    }
                }

                if (string.Equals(item.Name, foregroundApp, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Leak guard: sustained growth of PRIVATE memory. Trimming only relieves physical RAM;
                // the leaked memory stays committed until the app is restarted, so we say so.
                if (Config.AutoLeakRemediation &&
                    !item.IsProtected &&
                    item.IsLeaking &&
                    item.LeakRateMbPerMin >= Config.LeakVelocityThresholdMbMin &&
                    item.PrivateMb >= 300)
                {
                    if (!_lastLeakAction.TryGetValue(item.Name, out var lastLeak) || (now - lastLeak).TotalSeconds >= Config.LeakGuardCooldownSeconds)
                    {
                        _lastLeakAction[item.Name] = now;
                        OptimizeSingleApp(item.Name,
                            $"Leak Guard: private memory growing +{item.LeakRateMbPerMin:F0} MB/min — working set trimmed; restart the app to release leaked memory");
                    }
                }
            }
        }

        #endregion

        #region Commit charge (RAM + page file) warning

        private string _lastWarnedLevel = "ok";
        private int _lastWarningRank = 0;

        /// <summary>
        /// Trimming can't help here: committed memory counts against RAM + page file whether or not it is in RAM.
        /// When it nears the limit, new allocations fail ("out of memory", apps crash). On every rise the banner is
        /// shown; the Windows notification for the same level is not repeated within 30 minutes, so hovering around a
        /// threshold can't spam.
        /// </summary>
        private void CheckCommitPressure(MemorySnapshot snap)
        {
            string level = snap.CommitLevel;
            if (!Config.CommitWarningEnabled || level == "ok")
            {
                // While disabled, forget what was announced so re-enabling during pressure warns at once.
                _lastWarnedLevel = "ok";
                return;
            }

            int rank = level == "critical" ? 2 : 1;
            int lastRank = _lastWarnedLevel == "critical" ? 2 : (_lastWarnedLevel == "high" ? 1 : 0);
            if (rank <= lastRank)
            {
                _lastWarnedLevel = level; // steady or easing off: nothing new to say
                return;
            }

            _lastWarnedLevel = level;
            // The banner is always (re)shown on escalation; only the Windows notification is rate-limited.
            bool repeat = rank <= _lastWarningRank && (DateTime.Now - _lastCommitWarning).TotalMinutes < 30;
            var advice = BuildCommitAdvice(snap);
            advice.IsRepeat = repeat;
            if (!repeat)
            {
                _lastWarningRank = rank;
                _lastCommitWarning = DateTime.Now;
                Log.Warn($"Commit charge {snap.CommitPercent:F0}% ({snap.CommitTotalGb:F1}/{snap.CommitLimitGb:F1} GB): {advice.Title}");
            }
            try { OnCommitWarning?.Invoke(advice); } catch (Exception ex) { Log.Error("Commit warning handler failed", ex); }
        }

        private enum PageFileKind { None, SystemManaged, Custom, Unknown }

        /// <summary>Reads the page-file configuration (readable without admin rights).</summary>
        private static (PageFileKind Kind, int MaxMb) ReadPageFileSetup()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", false);
                if (key?.GetValue("PagingFiles") is not string[] entries) return (PageFileKind.Unknown, 0);
                var used = entries.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
                if (used.Count == 0) return (PageFileKind.None, 0);

                int maxTotal = 0;
                foreach (var e in used)
                {
                    // "?:\pagefile.sys" = automatic for all drives; "C:\pagefile.sys 0 0" or a bare path = system managed;
                    // "C:\pagefile.sys 4096 8192" = custom initial/maximum in MB.
                    var parts = e.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts[0].StartsWith("?:", StringComparison.Ordinal)) return (PageFileKind.SystemManaged, 0);
                    if (parts.Length < 3 || !int.TryParse(parts[2], out int max) || max == 0) return (PageFileKind.SystemManaged, 0);
                    maxTotal += max;
                }
                return (PageFileKind.Custom, maxTotal);
            }
            catch
            {
                return (PageFileKind.Unknown, 0);
            }
        }

        private CommitAdvice BuildCommitAdvice(MemorySnapshot snap)
        {
            string top = "";
            lock (_lock)
            {
                var biggest = _cachedGroupedProcesses
                    .Where(p => !IsCritical(p.Name))
                    .OrderByDescending(p => p.PrivateMb)
                    .FirstOrDefault();
                if (biggest != null && biggest.PrivateMb >= 300)
                    top = $" {biggest.Name} is using the most ({biggest.PrivateMb / 1024.0:F1} GB committed).";
            }

            var (kind, maxMb) = ReadPageFileSetup();
            string level = snap.CommitLevel;
            string usage = $"{snap.CommitTotalGb:F1} of {snap.CommitLimitGb:F1} GB committed ({snap.CommitPercent:F0}%).";
            string title = level == "critical" ? "Almost out of memory" : "Running low on memory";

            return kind switch
            {
                PageFileKind.None => new CommitAdvice
                {
                    Level = level, Title = title, SuggestPageFile = true,
                    Body = $"{usage} There is no page file, so apps will crash when this hits 100%. Turn on \"System managed size\" in page-file settings.{top}"
                },
                PageFileKind.Custom => new CommitAdvice
                {
                    Level = level, Title = title, SuggestPageFile = true,
                    Body = $"{usage} Your page file is capped at {maxMb / 1024.0:F1} GB. Raise the maximum or choose \"System managed size\".{top}"
                },
                _ => new CommitAdvice
                {
                    Level = level, Title = title, SuggestPageFile = kind == PageFileKind.Unknown,
                    Body = $"{usage} Freeing RAM won't help — close or restart apps you don't need.{top}"
                }
            };
        }

        #endregion

        #region Sampling

        /// <summary>Returns the latest snapshot. Cheap: the background loop keeps it fresh, so the UI thread never scans processes.</summary>
        public MemorySnapshot GetSnapshot() => _lastSnapshot;

        /// <summary>Returns the cached process list (refreshed by the background loop every ~2 s).</summary>
        public List<ProcessItem> GetProcesses(bool grouped = true)
        {
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
                return (pid, ExeName(proc.ProcessName));
            }
            catch
            {
                return (0, "");
            }
        }

        private MemorySnapshot SampleTelemetryAndProcesses(bool forceProcessScan, bool recordTelemetry)
        {
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref memStatus);

            var perfInfo = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
            GetPerformanceInfo(out perfInfo, perfInfo.cb);

            const double GB = 1024.0 * 1024.0 * 1024.0;
            double pageSize = perfInfo.PageSize.ToUInt64() > 0 ? perfInfo.PageSize.ToUInt64() : 4096.0;
            double totalGb = memStatus.ullTotalPhys / GB;
            double availGb = memStatus.ullAvailPhys / GB;
            double usedGb = Math.Max(0.0, totalGb - availGb);
            double percent = totalGb > 0 ? (usedGb / totalGb) * 100.0 : memStatus.dwMemoryLoad;

            double standbyGb;
            double freeGb;
            bool listsMeasured = false;
            if (_memCounters != null && _memCounters.TryRead(out double standbyBytes, out double freeBytes))
            {
                standbyGb = standbyBytes / GB;
                freeGb = freeBytes / GB;
                listsMeasured = true;
            }
            else
            {
                // Fallback estimate: system file-cache working set. Flagged via MemoryListsMeasured = false.
                standbyGb = Math.Min(availGb, (perfInfo.SystemCache.ToUInt64() * pageSize) / GB);
                freeGb = Math.Max(0.0, availGb - standbyGb);
            }

            // Kernel memory lists (same data as System Informer's "Memory lists"): standby per priority and the
            // cumulative count of standby pages Windows has repurposed. Needs SeProfileSingleProcessPrivilege.
            double[]? standbyByPriority = null;
            if (_memListQueryFailed && DateTime.Now >= _memListRetryAt) _memListQueryFailed = false;
            if (_privilegesEnabled && !_memListQueryFailed)
            {
                try
                {
                    var ml = new SYSTEM_MEMORY_LIST_INFORMATION
                    {
                        PageCountByPriority = new UIntPtr[8],
                        RepurposedPagesByPriority = new UIntPtr[8]
                    };
                    int status = NtQuerySystemInformation(SystemMemoryListInformation, ref ml, Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>(), out _);
                    if (status == 0)
                    {
                        standbyByPriority = new double[8];
                        ulong repurposed = 0;
                        for (int i = 0; i < 8; i++)
                        {
                            standbyByPriority[i] = ml.PageCountByPriority[i].ToUInt64() * pageSize / GB;
                            repurposed += ml.RepurposedPagesByPriority[i].ToUInt64();
                        }
                        var now = DateTime.Now;
                        lock (_repurposeLock)
                        {
                        bool advance = _lastRepurposeSample == DateTime.MinValue || repurposed < _lastRepurposedPages;
                        if (!advance)
                        {
                            double minutes = (now - _lastRepurposeSample).TotalMinutes;
                            if (minutes >= 0.02)
                            {
                                double mbPerMin = (repurposed - _lastRepurposedPages) * pageSize / (1024.0 * 1024.0) / minutes;
                                // Smooth it so one burst doesn't flip the decision (weight grows with the interval).
                                double w = Math.Clamp(minutes / 0.25, 0.1, 1.0) * 0.5;
                                _repurposeMbPerMin = _repurposeMbPerMin < 0 ? mbPerMin : _repurposeMbPerMin * (1 - w) + mbPerMin * w;
                                advance = true;
                            }
                        }
                        if (advance)
                        {
                            _lastRepurposedPages = repurposed;
                            _lastRepurposeSample = now;
                        }
                        }
                    }
                    else
                    {
                        _memListQueryFailed = true;
                        _memListRetryAt = DateTime.Now.AddMinutes(5);
                    }
                }
                catch
                {
                    _memListQueryFailed = true;
                    _memListRetryAt = DateTime.Now.AddMinutes(5);
                }
            }

            // Purging standby only helps when free memory is actually scarce: otherwise Windows hands out free
            // pages anyway and the purge just throws away useful cache. Repurposing confirms real pressure.
            bool purgeWorthwhile = MemoryPolicy.StandbyPurgeWorthwhile(totalGb, standbyGb, freeGb, _repurposeMbPerMin);

            double commitTotalGb = (perfInfo.CommitTotal.ToUInt64() * pageSize) / GB;
            double commitLimitGb = Math.Max(0.1, (perfInfo.CommitLimit.ToUInt64() * pageSize) / GB);
            double commitPct = (commitTotalGb / commitLimitGb) * 100.0;
            // Hysteresis so the warning doesn't flicker around a threshold.
            string commitLevel = MemoryPolicy.NextCommitLevel(_commitLevel, commitPct);
            _commitLevel = commitLevel;

            double pfTotalGb = Math.Max(0.0, (memStatus.ullTotalPageFile / GB) - totalGb);
            double pfAvailGb = Math.Max(0.0, (memStatus.ullAvailPageFile / GB) - availGb);
            double pfUsedGb = Math.Max(0.0, pfTotalGb - pfAvailGb);
            double pfPct = pfTotalGb > 0.05 ? Math.Min(100.0, (pfUsedGb / pfTotalGb) * 100.0) : 0.0;

            double kernelTotalMb = (perfInfo.KernelTotal.ToUInt64() * pageSize) / (1024.0 * 1024.0);
            double kernelPagedMb = (perfInfo.KernelPaged.ToUInt64() * pageSize) / (1024.0 * 1024.0);
            double kernelNonPagedMb = (perfInfo.KernelNonpaged.ToUInt64() * pageSize) / (1024.0 * 1024.0);

            var fg = GetForegroundProcessInfo();

            if (forceProcessScan || (DateTime.Now - _lastProcessScan).TotalMilliseconds >= ScanIntervalMs)
            {
                RefreshProcessSnapshots(fg.Name);
            }

            string pressure = percent >= 85 ? "CRITICAL" : (percent >= 72 ? "ELEVATED" : "OPTIMAL");

            // Resolve effective profile & threshold
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
                StandbyAndCacheGb = Math.Round(standbyGb, 2),
                FreeExactGb = Math.Round(freeGb, 2),
                MemoryListsMeasured = listsMeasured,
                StandbyByPriorityGb = standbyByPriority,
                RepurposeMbPerMin = Math.Round(_repurposeMbPerMin, 0),
                StandbyPurgeWorthwhile = purgeWorthwhile,
                CommitLevel = commitLevel,
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
                NextScheduledSeconds = nextSchedSec,
                EffectiveCooldownSeconds = EffectiveCooldownSeconds()
            };

            if (recordTelemetry)
            {
                lock (_lock)
                {
                    _telemetry.Add(new TelemetryPoint
                    {
                        TimeLabel = DateTime.Now.ToString("HH:mm:ss"),
                        UsedPercent = snapshot.Percent,
                        CpuPercent = snapshot.CpuPercent,
                        UsedGb = snapshot.UsedGb,
                        StandbyGb = snapshot.StandbyAndCacheGb
                    });
                    // One point per 1.5 s tick → 40 points ≈ the 60-second window the chart advertises.
                    while (_telemetry.Count > 40)
                        _telemetry.RemoveAt(0);
                }
            }

            _lastSnapshot = snapshot;
            return snapshot;
        }

        private bool IsUserOrProfileShielded(string exeName)
        {
            lock (_stateLock)
            {
                return Config.ProtectedApps.Any(x => string.Equals(x, exeName, StringComparison.OrdinalIgnoreCase))
                    || _profileShields.Contains(exeName);
            }
        }

        private bool IsShielded(string exeName) => IsCritical(exeName) || IsUserOrProfileShielded(exeName);

        private void RefreshProcessSnapshots(string foregroundApp, bool waitForLock = false)
        {
            if (waitForLock) Monitor.Enter(_scanLock);
            else if (!Monitor.TryEnter(_scanLock))
                return;
            try
            {
                _lastProcessScan = DateTime.Now;
                var now = DateTime.Now;
                int coreCount = Math.Max(1, Environment.ProcessorCount);

                var rawProcesses = Process.GetProcesses();
                var individual = new List<ProcessItem>(rawProcesses.Length);
                var runningExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenPids = new HashSet<int>();
                var pidCpu = new Dictionary<int, double>(rawProcesses.Length);

                HashSet<string> frozen, eco;
                Dictionary<string, int> caps;
                lock (_stateLock)
                {
                    frozen = new HashSet<string>(_frozenApps, StringComparer.OrdinalIgnoreCase);
                    eco = new HashSet<string>(_ecoApps, StringComparer.OrdinalIgnoreCase);
                    caps = new Dictionary<string, int>(Config.AppMemoryCapsMb, StringComparer.OrdinalIgnoreCase);
                }
                bool protectFg = Config.ProtectForeground;

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

                        string name = ExeName(proc.ProcessName);
                        runningExeNames.Add(name);
                        seenPids.Add(pid);

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
                                    pidCpu[pid] = procCpuPct;
                                }
                            }
                            _procCpuSamples[pid] = (now, totalProcTime);
                        }
                        catch
                        {
                            // Protected process CPU time inaccessible
                        }

                        double wsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
                        if (wsMb < 4.0) continue;

                        double privMb = proc.PrivateMemorySize64 / (1024.0 * 1024.0);
                        double peakMb = proc.PeakWorkingSet64 / (1024.0 * 1024.0);

                        bool isFg = string.Equals(name, foregroundApp, StringComparison.OrdinalIgnoreCase);
                        bool isProt = IsShielded(name);
                        bool isFrozen = frozen.Contains(name);
                        bool isEco = eco.Contains(name);
                        int? capMb = caps.TryGetValue(name, out int c) ? c : null;

                        int score = ComputeSmartScore(wsMb, procCpuPct, 0.0, isFg, isProt);
                        var (badge, color) = ResolveBadge(isFrozen, isEco, isFg, isProt, false, procCpuPct, score);

                        individual.Add(new ProcessItem
                        {
                            Name = name,
                            Pid = pid,
                            ProcessCount = 1,
                            MemoryMb = Math.Round(wsMb, 1),
                            PrivateMb = Math.Round(privMb, 1),
                            PeakMemoryMb = Math.Round(peakMb, 1),
                            CpuPercent = Math.Round(procCpuPct, 1),
                            IsForeground = isFg,
                            IsProtected = isProt,
                            IsFrozen = isFrozen,
                            IsEco = isEco,
                            CapMb = capMb,
                            SmartScore = score,
                            ReclaimableMb = (isProt || (isFg && protectFg)) ? 0.0 : Math.Round(wsMb, 1),
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

                // System CPU %: GetSystemTimes covers every process (including ones we can't open) and isn't
                // skewed by processes starting or exiting. Kernel time includes idle time.
                if (GetSystemTimes(out FILETIME idleFt, out FILETIME kernelFt, out FILETIME userFt))
                {
                    ulong idle = idleFt.Value, kernel = kernelFt.Value, user = userFt.Value;
                    if (_lastSysKernel != 0)
                    {
                        double total = (double)(kernel - _lastSysKernel) + (user - _lastSysUser);
                        double idleDelta = idle - _lastSysIdle;
                        if (total > 0)
                            _cachedSystemCpuPercent = Math.Clamp((1.0 - idleDelta / total) * 100.0, 0.0, 100.0);
                    }
                    _lastSysIdle = idle;
                    _lastSysKernel = kernel;
                    _lastSysUser = user;
                }
                else if (_lastCpuSampleTime != DateTime.MinValue && totalCpuSum > _lastTotalProcCpu)
                {
                    double cpuTotalDelta = (totalCpuSum - _lastTotalProcCpu).TotalSeconds;
                    _cachedSystemCpuPercent = Math.Min(100.0, Math.Max(1.0, (cpuTotalDelta / (cpuDeltaSeconds * coreCount)) * 100.0));
                }
                _lastTotalProcCpu = totalCpuSum;
                _lastCpuSampleTime = now;
                _lastPidCpu = pidCpu;

                // Drop per-PID / per-app history for processes that have exited (prevents unbounded growth).
                foreach (var stalePid in _procCpuSamples.Keys.Where(k => !seenPids.Contains(k)).ToList())
                    _procCpuSamples.Remove(stalePid);
                foreach (var staleApp in _appHistory.Keys.Where(k => !runningExeNames.Contains(k)).ToList())
                    _appHistory.Remove(staleApp);

                // Group by application name
                var grouped = individual
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        string appName = g.First().Name;
                        double sumMb = g.Sum(x => x.MemoryMb);
                        double sumPriv = g.Sum(x => x.PrivateMb);
                        double peakMb = g.Max(x => x.PeakMemoryMb);
                        double sumCpu = Math.Min(100.0, g.Sum(x => x.CpuPercent));
                        bool isFg = g.Any(x => x.IsForeground);
                        bool isProt = g.Any(x => x.IsProtected);
                        bool isFrozen = frozen.Contains(appName);
                        bool isEco = eco.Contains(appName);
                        int? capMb = caps.TryGetValue(appName, out int c) ? c : null;

                        double leakRate = UpdateAndCalculateLeakRate(appName, sumPriv, now);
                        bool isLeaking = leakRate >= Config.LeakVelocityThresholdMbMin && sumPriv >= 200;

                        int score = ComputeSmartScore(sumMb, sumCpu, leakRate, isFg, isProt);
                        var (badge, color) = ResolveBadge(isFrozen, isEco, isFg, isProt, isLeaking, sumCpu, score);

                        return new ProcessItem
                        {
                            Name = appName,
                            Pid = g.First().Pid,
                            ProcessCount = g.Count(),
                            MemoryMb = Math.Round(sumMb, 1),
                            PrivateMb = Math.Round(sumPriv, 1),
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
                            ReclaimableMb = (isProt || (isFg && protectFg)) ? 0.0 : Math.Round(sumMb, 1),
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

                // Auto-detected workload profiles. Shields for matched EXEs live only while the profile is
                // active; they are never written into the user's ProtectedApps list.
                if (Config.ProfilesAutoSwitch)
                {
                    WorkloadProfile? matched = null;
                    var shields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prof in Config.Profiles.Where(p => p.Enabled && p.MatchExes.Count > 0))
                    {
                        if (prof.MatchExes.Any(exe => runningExeNames.Contains(exe)))
                        {
                            matched = prof;
                            if (prof.AutoShieldMatched)
                            {
                                foreach (var exe in prof.MatchExes.Where(e => runningExeNames.Contains(e)))
                                    shields.Add(exe);
                            }
                            break;
                        }
                    }

                    lock (_stateLock)
                    {
                        _profileShields = shields;
                    }

                    if (matched != null && matched.BoostOnLaunch && _lastLaunchedProfileId != matched.Id)
                    {
                        _lastLaunchedProfileId = matched.Id;
                        _lastAutoOptimizeTime = DateTime.Now;
                        var launchProfile = matched;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            if (!_disposed)
                                OptimizeMemory(launchProfile.AutoMode, "Profile Launch Boost", $"Auto-activated '{launchProfile.Name}' profile");
                        });
                    }
                    else if (matched == null)
                    {
                        _lastLaunchedProfileId = "";
                    }

                    _activeMatchedProfile = matched;
                }
                else
                {
                    lock (_stateLock)
                    {
                        _profileShields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    _activeMatchedProfile = null;
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

        /// <summary>
        /// Sustained private-memory growth in MB/min over a rolling 2-minute window. Requires most
        /// samples to be rising so one-off allocations (opening a file, loading a level) don't count.
        /// </summary>
        private double UpdateAndCalculateLeakRate(string appName, double privateMb, DateTime now)
        {
            if (!_appHistory.TryGetValue(appName, out var list))
            {
                list = new List<(DateTime, double)>();
                _appHistory[appName] = list;
            }

            list.Add((now, privateMb));
            list.RemoveAll(x => (now - x.Time).TotalSeconds > 120);

            if (list.Count < 6) return 0.0;
            var oldest = list[0];
            double dtMin = (now - oldest.Time).TotalMinutes;
            if (dtMin < 0.5) return 0.0;

            double deltaMb = privateMb - oldest.PrivateMb;
            if (deltaMb <= 20.0) return 0.0;

            int rising = 0;
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].PrivateMb >= list[i - 1].PrivateMb - 1.0) rising++;
            }
            if (rising < (list.Count - 1) * 0.8) return 0.0;

            return deltaMb / dtMin;
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

        #endregion

        #region Core Memory Optimization Operations

        /// <summary>
        /// Modes:
        ///  smart   — trims idle (low-CPU) background processes above MinProcessWorkingSetMb.
        ///  deep    — trims every eligible process; with admin also combines pages, flushes the modified list
        ///            and purges the LOW-priority standby list.
        ///  standby — admin only: flushes the modified list and purges the whole standby list.
        ///  max     — deep trim + page combine + modified flush + full standby purge (admin for the kernel part).
        /// Shielded, critical and (optionally) foreground processes are never trimmed in any mode.
        /// </summary>
        public OptimizationEvent OptimizeMemory(string mode = "smart", string trigger = "Manual", string reason = "Manual optimization")
        {
            lock (_optimizeLock)
            {
                using var boost = SweepBoost.Begin();
                return OptimizeMemoryCore(mode, trigger, reason);
            }
        }

        /// <summary>Sweeps PulseRAM started on its own (as opposed to the user clicking, a hotkey or the tray menu).</summary>
        private static bool IsAutomaticTrigger(string trigger) =>
            trigger is "Auto-Pilot" or "Scheduled" or "Profile Launch Boost";

        private OptimizationEvent OptimizeMemoryCore(string mode, string trigger, string reason)
        {
            var sw = Stopwatch.StartNew();
            var beforeSnap = SampleTelemetryAndProcesses(forceProcessScan: false, recordTelemetry: false);
            var fg = GetForegroundProcessInfo();

            string normMode = (mode ?? "smart").ToLowerInvariant();
            if (!IsKnownMode(normMode)) normMode = "smart";
            bool wantsKernel = normMode is "deep" or "standby" or "max";
            // Smart standby purge: on automatic sweeps, skip the cache purge unless it would actually help.
            bool skipPurgeNoPressure = wantsKernel && _privilegesEnabled && Config.SmartStandbyPurge
                && IsAutomaticTrigger(trigger) && !beforeSnap.StandbyPurgeWorthwhile;
            string noPressureNote = beforeSnap.StandbyAndCacheGb < Math.Max(0.5, beforeSnap.TotalGb * 0.10)
                ? $"Standby purge skipped: the cache is small ({beforeSnap.StandbyAndCacheGb:F1} GB), so there is little to gain."
                : $"Standby purge skipped: {beforeSnap.FreeExactGb:F1} GB already free and Windows isn't evicting cache, so purging would only discard useful cache.";

            if (normMode == "standby" && skipPurgeNoPressure)
            {
                // Nothing worth doing right now: not logged or announced, and the next tick simply re-checks.
                return new OptimizationEvent
                {
                    Mode = "STANDBY",
                    Trigger = trigger,
                    Reason = reason,
                    BeforePercent = beforeSnap.Percent,
                    AfterPercent = beforeSnap.Percent,
                    DurationMs = sw.ElapsedMilliseconds,
                    Skipped = true,
                    SkippedNoPressure = true,
                    Note = noPressureNote
                };
            }

            if (normMode == "standby" && !_privilegesEnabled)
            {
                return RecordEvent(new OptimizationEvent
                {
                    Mode = "STANDBY",
                    Trigger = trigger,
                    Reason = reason,
                    BeforePercent = beforeSnap.Percent,
                    AfterPercent = beforeSnap.Percent,
                    DurationMs = sw.ElapsedMilliseconds,
                    Skipped = true,
                    Note = "Standby purge needs administrator rights — nothing was changed."
                }, countAsOptimization: false, trigger);
            }

            int trimmedCount = 0;
            double trimmedMb = 0.0;
            bool standbyPurged = false;
            bool modifiedFlushed = false;
            bool pagesCombined = false;
            double combinedMb = 0;

            if (normMode is "smart" or "deep" or "max")
            {
                // Smart mode needs per-process CPU deltas (two scans). Right after launch only the baseline
                // scan exists, so take the second one now instead of trimming nothing.
                if (normMode == "smart" && _lastPidCpu.Count == 0)
                    RefreshProcessSnapshots(fg.Name, waitForLock: true);
                var pidCpu = _lastPidCpu;
                double minWs = Config.MinProcessWorkingSetMb;
                double idleCeiling = Config.IdleCpuCeilingPercent;
                bool protectFg = Config.ProtectForeground;

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.Id <= 4) continue;
                        string name = ExeName(proc.ProcessName);

                        if (IsShielded(name))
                            continue;
                        if (protectFg && string.Equals(name, fg.Name, StringComparison.OrdinalIgnoreCase))
                            continue;

                        long wsBefore = proc.WorkingSet64;
                        if (normMode == "smart")
                        {
                            if (wsBefore / (1024.0 * 1024.0) < minWs)
                                continue;
                            // "Idle only": skip processes we have no CPU sample for, or that are busy.
                            if (!pidCpu.TryGetValue(proc.Id, out double cpu) || cpu > idleCeiling)
                                continue;
                        }

                        IntPtr hProc = OpenProcess(TRIM_ACCESS, false, proc.Id);
                        if (hProc == IntPtr.Zero) continue;
                        try
                        {
                            bool ok = EmptyWorkingSet(hProc)
                                || SetProcessWorkingSetSizeEx(hProc, new IntPtr(-1), new IntPtr(-1), 0);
                            if (!ok) continue;
                        }
                        finally
                        {
                            CloseHandle(hProc);
                        }

                        trimmedCount++;
                        proc.Refresh();
                        trimmedMb += Math.Max(0, wsBefore - proc.WorkingSet64) / (1024.0 * 1024.0);
                    }
                    catch
                    {
                        // Ignore protected / exited process
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
                // Note: the system-wide MemoryEmptyWorkingSets command is deliberately NOT used in "max":
                // it would also trim shielded, critical and foreground processes.
            }

            if (wantsKernel && _privilegesEnabled)
            {
                if (normMode is "deep" or "max")
                {
                    var combineInfo = new MEMORY_COMBINE_INFORMATION_EX();
                    if (NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, ref combineInfo, Marshal.SizeOf<MEMORY_COMBINE_INFORMATION_EX>()) == 0)
                    {
                        pagesCombined = true;
                        combinedMb = combineInfo.PagesCombined.ToUInt64() * (double)Environment.SystemPageSize / (1024.0 * 1024.0);
                    }
                }

                if (!skipPurgeNoPressure)
                {
                    int cmdFlush = MemoryFlushModifiedList;
                    if (NtSetSystemInformation(SystemMemoryListInformation, ref cmdFlush, sizeof(int)) == 0)
                        modifiedFlushed = true;

                    int cmdStandby = normMode == "deep" ? MemoryPurgeLowPriorityStandbyList : MemoryPurgeStandbyList;
                    if (NtSetSystemInformation(SystemMemoryListInformation, ref cmdStandby, sizeof(int)) == 0)
                        standbyPurged = true;
                }
            }

            Thread.Sleep(150);
            var afterSnap = SampleTelemetryAndProcesses(forceProcessScan: true, recordTelemetry: false);
            sw.Stop();

            double availGainMb = Math.Max(0.0, (afterSnap.AvailableGb - beforeSnap.AvailableGb) * 1024.0);
            double freeGainMb = (beforeSnap.MemoryListsMeasured && afterSnap.MemoryListsMeasured)
                ? Math.Max(0.0, (afterSnap.FreeExactGb - beforeSnap.FreeExactGb) * 1024.0)
                : 0.0;

            string note = "";
            if (wantsKernel && !_privilegesEnabled)
                note = "Kernel purge skipped — run PulseRAM as administrator to purge the standby list.";
            else if (skipPurgeNoPressure)
                note = noPressureNote;

            return RecordEvent(new OptimizationEvent
            {
                Mode = normMode.ToUpperInvariant(),
                Trigger = trigger,
                Reason = reason,
                // Pure standby purge trims nothing; its measurable effect is pages moved to the free list.
                FreedMb = Math.Round(normMode == "standby" ? freeGainMb : trimmedMb, 1),
                AvailableGainMb = Math.Round(availGainMb, 1),
                FreeListGainMb = Math.Round(freeGainMb, 1),
                BeforePercent = beforeSnap.Percent,
                AfterPercent = afterSnap.Percent,
                ProcessesTrimmed = trimmedCount,
                DurationMs = sw.ElapsedMilliseconds,
                StandbyPurged = standbyPurged,
                ModifiedFlushed = modifiedFlushed,
                PagesCombined = pagesCombined,
                CombinedMb = Math.Round(combinedMb, 1),
                Note = note
            }, countAsOptimization: true, trigger);
        }

        private OptimizationEvent RecordEvent(OptimizationEvent ev, bool countAsOptimization, string trigger)
        {
            if (countAsOptimization)
            {
                // The session counter is "working set trimmed"; a pure standby purge trims nothing.
                if (ev.Mode != "STANDBY") _sessionFreedMb += ev.FreedMb;
                _optimizationCount++;
            }

            lock (_lock)
            {
                _events.Insert(0, ev);
                while (_events.Count > 40)
                    _events.RemoveAt(_events.Count - 1);
            }
            Log.Info($"{ev.Mode} [{ev.Trigger}] {ev.Reason} → trimmed {ev.FreedMb:F0} MB from {ev.ProcessesTrimmed}, " +
                     $"avail +{ev.AvailableGainMb:F0} MB, free +{ev.FreeListGainMb:F0} MB, standby={ev.StandbyPurged}, {ev.DurationMs} ms" +
                     (string.IsNullOrEmpty(ev.Note) ? "" : $" · {ev.Note}"));

            if (countAsOptimization && Config.AutoSoundChime && trigger != "Manual")
            {
                try { MessageBeep(0x40); } catch { }
            }

            // Refresh the cached snapshot's session counters for the UI.
            var s = _lastSnapshot;
            s.SessionFreedMb = Math.Round(_sessionFreedMb, 1);
            s.OptimizationCount = _optimizationCount;

            OnOptimizationCompleted?.Invoke(ev);
            return ev;
        }

        /// <summary>
        /// Applies a HARD per-process working-set maximum (QUOTA_LIMITS_HARDWS_MAX_ENABLE) to every process of
        /// an app. With <paramref name="force"/> false, only processes that don't already carry the cap are
        /// touched, so repeated checks cost nothing and never re-trim capped processes.
        /// Returns the number of processes the cap was applied to.
        /// </summary>
        private int EnforceHardCap(string appName, int capMb, bool force)
        {
            lock (_optimizeLock)
            {
                var sw = Stopwatch.StartNew();
                long capBytes = capMb * 1024L * 1024L;
                int applied = 0;
                int refused = 0;
                double trimmedMb = 0;
                double beforePct = _lastSnapshot.Percent;

                foreach (var proc in Process.GetProcessesByName(BaseName(appName)))
                {
                    try
                    {
                        if (!InUserSession(proc)) { refused++; continue; }
                        IntPtr h = OpenProcess(TRIM_ACCESS, false, proc.Id);
                        if (h == IntPtr.Zero) { refused++; continue; }
                        try
                        {
                            bool needs = force;
                            if (!needs)
                            {
                                needs = !GetProcessWorkingSetSizeEx(h, out _, out IntPtr curMax, out uint flags)
                                    || (flags & QUOTA_LIMITS_HARDWS_MAX_ENABLE) == 0
                                    || curMax.ToInt64() != capBytes;
                            }
                            if (!needs) continue;

                            long wsBefore = proc.WorkingSet64;
                            if (SetProcessWorkingSetSizeEx(h, new IntPtr(1024 * 1024), new IntPtr(capBytes),
                                    QUOTA_LIMITS_HARDWS_MAX_ENABLE | QUOTA_LIMITS_HARDWS_MIN_DISABLE))
                            {
                                applied++;
                                proc.Refresh();
                                trimmedMb += Math.Max(0, wsBefore - proc.WorkingSet64) / (1024.0 * 1024.0);
                            }
                            else
                            {
                                refused++;
                            }
                        }
                        finally
                        {
                            CloseHandle(h);
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }

                if (applied > 0 || (force && refused > 0))
                {
                    sw.Stop();
                    RecordEvent(new OptimizationEvent
                    {
                        Mode = "APP CAP",
                        Trigger = "Targeted",
                        Reason = $"{appName}: {capMb} MB hard working-set cap per process",
                        FreedMb = Math.Round(trimmedMb, 1),
                        BeforePercent = beforePct,
                        AfterPercent = _lastSnapshot.Percent,
                        ProcessesTrimmed = applied,
                        DurationMs = sw.ElapsedMilliseconds,
                        Skipped = applied == 0,
                        Note = refused > 0 ? $"{refused} process(es) refused the cap (access denied)." : ""
                    }, countAsOptimization: applied > 0, "Targeted");
                }
                return applied;
            }
        }

        /// <summary>Trims one application's processes (working-set trim only).</summary>
        #region Leak history

        public List<LeakIncident> GetLeakIncidents()
        {
            lock (_stateLock) return _leakHistory.Snapshot();
        }

        /// <summary>Removes an app from the list (it comes back if it starts leaking again).</summary>
        public void DismissLeak(string app)
        {
            lock (_stateLock) _leakHistory.Dismiss(app, DateTime.Now);
        }

        /// <summary>Forgets an app entirely (e.g. after it was restarted: the new process starts with a clean slate).</summary>
        public void ForgetLeak(string app)
        {
            lock (_stateLock) _leakHistory.Forget(app);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        /// <summary>Removes an app from the list and stops tracking it for good (undo by editing LeakIgnoredApps in the config).</summary>
        public void IgnoreLeak(string app)
        {
            lock (_stateLock)
            {
                _leakHistory.Forget(app);
                if (!Config.LeakIgnoredApps.Contains(app, StringComparer.OrdinalIgnoreCase))
                    Config.LeakIgnoredApps.Add(BaseName(app));
            }
            SaveConfig();
        }

        /// <summary>
        /// Restarts an app the way a user would: asks each of its windows to close (so it can save or prompt),
        /// waits up to 15 s, then starts it again. Never force-kills, so no unsaved work is thrown away.
        /// Blocking; call it off the UI thread.
        /// </summary>
        public (bool Ok, string Message) RestartApp(string app)
        {
            string name = BaseName(app);
            if (IsUnfreezable(name) || string.Equals(name, "PulseRAM", StringComparison.OrdinalIgnoreCase))
                return (false, $"{name} is part of Windows, so PulseRAM won't restart it. Signing out and back in (or restarting the PC) releases its memory.");

            var procs = Process.GetProcessesByName(name);
            try
            {
                if (procs.Length == 0)
                {
                    ForgetLeak(app);
                    return (true, $"{name} is no longer running.");
                }

                string? path = null;
                foreach (var p in procs)
                {
                    try { path ??= p.MainModule?.FileName; } catch { }
                }

                int asked = 0;
                foreach (var p in procs)
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow()) asked++; } catch { }
                }
                if (asked == 0)
                    return (false, $"{name} has no window PulseRAM can close. Close it yourself, then start it again.");

                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    if (procs.All(p => { try { return p.HasExited; } catch { return true; } })) break;
                    Thread.Sleep(300);
                }
                bool allExited = procs.All(p => { try { return p.HasExited; } catch { return true; } });
                if (!allExited)
                    return (false, $"{name} is still open (it may be asking to save). Finish closing it, then start it again.");

                ForgetLeak(app); // a fresh process: if it leaks again it should show up again
                if (path == null)
                    return (true, $"{name} closed. Start it again from the Start menu.");
                try
                {
                    if (IsAdmin)
                    {
                        // Don't hand our administrator rights to the app: ask the running (non-elevated) Explorer
                        // shell to start it, as if it was launched from the Start menu. Without a shell, a new
                        // explorer.exe would inherit our elevation, so leave the relaunch to the user then.
                        if (GetShellWindow() == IntPtr.Zero)
                            return (true, $"{name} closed. Start it again from the Start menu.");
                        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                        {
                            ArgumentList = { path },
                            UseShellExecute = false
                        });
                    }
                    else
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    }

                    // Explorer returns before the app starts, so confirm it actually came back.
                    var until = DateTime.UtcNow.AddSeconds(8);
                    bool back = false;
                    while (!back && DateTime.UtcNow < until)
                    {
                        Thread.Sleep(400);
                        var again = Process.GetProcessesByName(name);
                        back = again.Length > 0;
                        foreach (var p in again) p.Dispose();
                    }
                    Log.Info(back ? $"Restarted {name} to release leaked memory" : $"Closed {name}; it did not start again within 8 s");
                    return back
                        ? (true, $"{name} restarted.")
                        : (true, $"{name} closed, but Windows didn't start it again. Start it from the Start menu.");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not start {path} again", ex);
                    return (true, $"{name} closed but couldn't be started again. Start it from the Start menu.");
                }
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }

        #endregion

        public OptimizationEvent OptimizeSingleApp(string appName, string reason = "Single App Trim")
        {
            lock (_optimizeLock)
            {
                using var boost = SweepBoost.Begin();
                var sw = Stopwatch.StartNew();
                var beforeSnap = SampleTelemetryAndProcesses(forceProcessScan: false, recordTelemetry: false);

                if (IsCritical(appName))
                {
                    return RecordEvent(new OptimizationEvent
                    {
                        Mode = "APP TRIM",
                        Trigger = "Targeted",
                        Reason = $"{appName}: {reason}",
                        BeforePercent = beforeSnap.Percent,
                        AfterPercent = beforeSnap.Percent,
                        Skipped = true,
                        Note = $"{appName} is a critical system process and is never trimmed."
                    }, countAsOptimization: false, "Targeted");
                }

                int trimmedCount = 0;
                double trimmedMb = 0;

                foreach (var proc in Process.GetProcessesByName(BaseName(appName)))
                {
                    try
                    {
                        long wsBefore = proc.WorkingSet64;
                        IntPtr hProc = OpenProcess(TRIM_ACCESS, false, proc.Id);
                        if (hProc == IntPtr.Zero) continue;
                        bool ok;
                        try
                        {
                            ok = EmptyWorkingSet(hProc);
                        }
                        finally
                        {
                            CloseHandle(hProc);
                        }
                        if (!ok) continue;

                        trimmedCount++;
                        proc.Refresh();
                        trimmedMb += Math.Max(0, wsBefore - proc.WorkingSet64) / (1024.0 * 1024.0);
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }

                var afterSnap = SampleTelemetryAndProcesses(forceProcessScan: true, recordTelemetry: false);
                sw.Stop();

                return RecordEvent(new OptimizationEvent
                {
                    Mode = "APP TRIM",
                    Trigger = "Targeted",
                    Reason = $"{appName}: {reason}",
                    FreedMb = Math.Round(trimmedMb, 1),
                    AvailableGainMb = Math.Round(Math.Max(0.0, (afterSnap.AvailableGb - beforeSnap.AvailableGb) * 1024.0), 1),
                    BeforePercent = beforeSnap.Percent,
                    AfterPercent = afterSnap.Percent,
                    ProcessesTrimmed = trimmedCount,
                    DurationMs = sw.ElapsedMilliseconds,
                    Skipped = trimmedCount == 0,
                    Note = trimmedCount == 0 ? "No process could be trimmed (not running or access denied)." : ""
                }, countAsOptimization: trimmedCount > 0, "Targeted");
            }
        }

        #endregion

        #region Process Governors: Shield, Cryo-Freeze, EcoQoS, Memory Cap

        public bool ToggleProtectApp(string appName)
        {
            bool nowProtected;
            lock (_stateLock)
            {
                var existing = Config.ProtectedApps.FirstOrDefault(x => string.Equals(x, appName, StringComparison.OrdinalIgnoreCase));
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
            }
            SaveConfig();
            RequestRescan();
            return nowProtected;
        }

        private enum ResumeResult { Resumed, Gone, Failed }

        /// <summary>Resumes exactly the journaled PID (verified by name and start time).</summary>
        private static ResumeResult TryResumeRecord(FrozenRecord rec)
        {
            Process? proc = null;
            try
            {
                try { proc = Process.GetProcessById(rec.Pid); }
                catch (ArgumentException) { return ResumeResult.Gone; }

                if (!string.Equals(ExeName(proc.ProcessName), ExeName(rec.Name), StringComparison.OrdinalIgnoreCase))
                    return ResumeResult.Gone; // PID reused by another program
                if (rec.StartTicksUtc != 0 && TryGetStartTicks(proc) is long t && t != 0 && t != rec.StartTicksUtc)
                    return ResumeResult.Gone; // PID reused by a newer instance

                IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, rec.Pid);
                if (h == IntPtr.Zero) return ResumeResult.Failed;
                try
                {
                    return NtResumeProcess(h) == 0 ? ResumeResult.Resumed : ResumeResult.Failed;
                }
                finally
                {
                    CloseHandle(h);
                }
            }
            catch
            {
                return ResumeResult.Failed;
            }
            finally
            {
                proc?.Dispose();
            }
        }

        private static bool SameApp(FrozenRecord r, string appName) =>
            string.Equals(BaseName(r.Name), BaseName(appName), StringComparison.OrdinalIgnoreCase);

        public (bool Ok, bool Frozen, string Message) ToggleFreezeApp(string appName)
        {
            // One freeze/thaw at a time: two overlapping freezes would suspend a process twice, and a single
            // thaw would then leave it suspended.
            lock (_freezeLock)
            {
                if (_disposed) return (false, false, "PulseRAM is shutting down.");
                return ToggleFreezeCore(appName);
            }
        }

        private (bool Ok, bool Frozen, string Message) ToggleFreezeCore(string appName)
        {
            if (IsUnfreezable(appName))
            {
                return (false, false, $"{appName} is a critical system process and cannot be frozen.");
            }

            bool targetFreeze;
            List<FrozenRecord> existing;
            lock (_stateLock)
            {
                targetFreeze = !_frozenApps.Contains(appName);
                existing = _frozenRecords.Where(r => SameApp(r, appName)).ToList();
            }

            if (targetFreeze)
            {
                int affected = 0;
                int trimmed = 0;
                var newRecords = new List<FrozenRecord>();
                foreach (var proc in Process.GetProcessesByName(BaseName(appName)))
                {
                    try
                    {
                        if (!InUserSession(proc)) continue;
                        // QUERY_LIMITED is required for EmptyWorkingSet; without it the trim silently fails.
                        IntPtr hProc = OpenProcess(PROCESS_SUSPEND_RESUME | TRIM_ACCESS, false, proc.Id);
                        if (hProc == IntPtr.Zero) continue;
                        try
                        {
                            if (NtSuspendProcess(hProc) == 0)
                            {
                                affected++;
                                newRecords.Add(new FrozenRecord { Name = ExeName(appName), Pid = proc.Id, StartTicksUtc = TryGetStartTicks(proc) });
                                if (EmptyWorkingSet(hProc)) trimmed++;
                            }
                        }
                        finally
                        {
                            CloseHandle(hProc);
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }

                if (affected == 0)
                    return (false, false, $"Could not suspend {appName} (not running or access denied).");

                bool journaled;
                lock (_stateLock)
                {
                    _frozenApps.Add(appName);
                    _frozenRecords.AddRange(newRecords);
                    // Journal every frozen PID so a crash, kill or uninstall can never leave apps suspended.
                    journaled = WriteFrozenJournal(_frozenRecords.ToList());
                    if (!journaled)
                    {
                        _frozenApps.Remove(appName);
                        _frozenRecords.RemoveAll(r => newRecords.Contains(r));
                    }
                }
                if (!journaled)
                {
                    // Never keep apps suspended without a record that lets a crashed PulseRAM resume them.
                    foreach (var rec in newRecords) TryResumeRecord(rec);
                    return (false, false, $"Couldn't freeze {appName}: PulseRAM can't save its safety record in {DataDirectory}.");
                }
                RequestRescan();
                return (true, true, $"Froze {appName}: {affected} process(es) suspended, {trimmed} trimmed");
            }

            // Thaw: resume exactly the PIDs we suspended. Keep the records we could not resume (access denied)
            // so they are retried later instead of being forgotten.
            int resumed = 0;
            var keep = new List<FrozenRecord>();
            foreach (var rec in existing)
            {
                switch (TryResumeRecord(rec))
                {
                    case ResumeResult.Resumed: resumed++; break;
                    case ResumeResult.Failed: keep.Add(rec); break;
                    case ResumeResult.Gone: break;
                }
            }

            lock (_stateLock)
            {
                _frozenRecords.RemoveAll(r => SameApp(r, appName));
                _frozenRecords.AddRange(keep);
                if (keep.Count == 0) _frozenApps.Remove(appName);
                WriteFrozenJournal(_frozenRecords.ToList());
            }
            RequestRescan();

            if (keep.Count > 0)
                return (false, true, $"Resumed {resumed}, but {keep.Count} process(es) of {appName} refused (run PulseRAM as administrator).");
            return (true, false, $"Thawed {appName}: {resumed} process(es) resumed");
        }

        public int ThawAllApps()
        {
            lock (_freezeLock)
            {
                List<string> apps;
                lock (_stateLock)
                {
                    apps = _frozenApps.ToList();
                }

                int count = 0;
                foreach (var app in apps)
                {
                    if (ToggleFreezeCore(app).Ok) count++;
                }
                // Records that refused to resume stay in _frozenRecords and the journal for a later retry.
                return count;
            }
        }

        public (bool Ok, bool EcoActive, string Message) ToggleEcoApp(string appName)
        {
            if (IsCritical(appName))
                return (false, false, $"{appName} cannot be throttled.");
            if (_disposed)
                return (false, false, "PulseRAM is shutting down.");

            bool enableEco;
            lock (_stateLock)
            {
                enableEco = !_ecoApps.Contains(appName);
            }

            int affected = 0;
            foreach (var proc in Process.GetProcessesByName(BaseName(appName)))
            {
                try
                {
                    if (!InUserSession(proc)) continue;
                    IntPtr hProc = OpenProcess(PROCESS_SET_INFORMATION | TRIM_ACCESS, false, proc.Id);
                    if (hProc == IntPtr.Zero) continue;
                    try
                    {
                        bool prioOk = SetPriorityClass(hProc, enableEco ? IDLE_PRIORITY_CLASS : NORMAL_PRIORITY_CLASS);
                        var state = new PROCESS_POWER_THROTTLING_STATE
                        {
                            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                            StateMask = enableEco ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
                        };
                        bool qosOk = SetProcessInformation(hProc, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                        if (enableEco) EmptyWorkingSet(hProc);
                        if (prioOk || qosOk) affected++;
                    }
                    finally
                    {
                        CloseHandle(hProc);
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }

            if (enableEco && affected == 0)
                return (false, false, $"Could not throttle {appName} (not running or access denied).");

            lock (_stateLock)
            {
                if (enableEco) _ecoApps.Add(appName);
                else _ecoApps.Remove(appName);
            }

            SaveConfig();
            RequestRescan();
            return (true, enableEco, enableEco
                ? $"Enabled EcoQoS Efficiency Mode on {appName} ({affected} PIDs)"
                : $"Restored normal priority on {appName}");
        }

        public void SetAppMemoryCap(string appName, int? capMb)
        {
            if (capMb.HasValue && capMb.Value >= 64)
            {
                lock (_stateLock)
                {
                    Config.AppMemoryCapsMb[appName] = capMb.Value;
                    _lastCapAction[appName] = DateTime.Now;
                }
                EnforceHardCap(appName, capMb.Value, force: true);
            }
            else
            {
                lock (_stateLock)
                {
                    Config.AppMemoryCapsMb.Remove(appName);
                    _lastCapAction.Remove(appName);
                }
                ReleaseHardCap(appName);
            }
            SaveConfig();
            RequestRescan();
        }

        /// <summary>Turns the hard working-set maximum back into the normal soft limit.</summary>
        private static void ReleaseHardCap(string appName)
        {
            foreach (var proc in Process.GetProcessesByName(BaseName(appName)))
            {
                try
                {
                    IntPtr h = OpenProcess(TRIM_ACCESS, false, proc.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        if (GetProcessWorkingSetSizeEx(h, out IntPtr min, out IntPtr max, out _))
                        {
                            SetProcessWorkingSetSizeEx(h, min, max, QUOTA_LIMITS_HARDWS_MAX_DISABLE | QUOTA_LIMITS_HARDWS_MIN_DISABLE);
                        }
                    }
                    finally
                    {
                        CloseHandle(h);
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }

        private void RequestRescan()
        {
            // Run the rescan off the caller's (UI) thread.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!_disposed) RefreshProcessSnapshots(GetForegroundProcessInfo().Name);
                }
                catch { }
            });
        }

        #endregion

        #region Windows Startup (HKCU Run key or elevated scheduled task)

        private const string StartupRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "PulseRAM";
        public const string ElevatedStartupTaskName = "PulseRAM Elevated Startup";

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

        public bool ElevatedStartupTaskExists() => RunSchtasks($"/Query /TN \"{ElevatedStartupTaskName}\"") == 0;

        public bool IsStartupEnabled() => CheckWindowsStartupRegistry() || ElevatedStartupTaskExists();

        /// <summary>Kept for compatibility: enables/disables startup and returns whether it succeeded.</summary>
        public bool SetWindowsStartupRegistry(bool enable) => SetStartup(enable).Ok;

        /// <summary>
        /// Enables or disables launch at logon. When PulseRAM is running as administrator, startup uses an
        /// elevated Task Scheduler task so kernel purges keep working after a reboot (a Run-key entry always
        /// starts as a standard user). Otherwise it falls back to the per-user Run key.
        /// </summary>
        public (bool Ok, bool Elevated, string Message) SetStartup(bool enable)
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PulseRAM.exe");

            try
            {
                if (enable)
                {
                    bool protectedLocation = InstallSecurity.IsAdminOnlyWritable(exePath);
                    if (_isAdmin && protectedLocation && CreateElevatedStartupTask(exePath))
                    {
                        // Remove the Run-key entry: a non-elevated copy launched by it could win the
                        // single-instance race and the elevated one would exit.
                        SetRunKey(false, exePath);
                        Config.RunOnStartup = true;
                        SaveConfig();
                        return (true, true, "PulseRAM will start elevated in the tray at logon (Task Scheduler).");
                    }

                    SetRunKey(true, exePath);
                    Config.RunOnStartup = true;
                    SaveConfig();
                    return (true, false, !_isAdmin
                        ? "Starts at logon as a standard user. Run PulseRAM as administrator and re-enable to start elevated."
                        : !protectedLocation
                            ? "Starts at logon as a standard user. PulseRAM is installed where other programs can change its files, so it won't start itself as administrator. Install it for all users (in Program Files) to allow that."
                            : "Starts at logon as a standard user (the elevated task could not be created).");
                }
                else
                {
                    SetRunKey(false, exePath);
                    bool taskRemoved = true;
                    if (ElevatedStartupTaskExists())
                        taskRemoved = RunSchtasks($"/Delete /TN \"{ElevatedStartupTaskName}\" /F") == 0;

                    Config.RunOnStartup = !taskRemoved;
                    SaveConfig();
                    return taskRemoved
                        ? (true, false, "Removed PulseRAM from Windows startup.")
                        : (false, true, "The elevated startup task can only be removed while PulseRAM runs as administrator.");
                }
            }
            catch (Exception ex)
            {
                return (false, false, $"Could not change startup: {ex.Message}");
            }
        }

        private static void SetRunKey(bool enable, string exePath)
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegPath, true);
            if (key == null) return;
            if (enable) key.SetValue(StartupValueName, $"\"{exePath}\" --tray");
            else key.DeleteValue(StartupValueName, false);
        }

        private static bool CreateElevatedStartupTask(string exePath)
        {
            string user;
            using (var id = WindowsIdentity.GetCurrent()) user = id.Name;

            // XML (rather than schtasks flags) so the task also starts on battery power and never times out.
            string xml =
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Starts PulseRAM elevated in the system tray at logon.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <Enabled>true</Enabled>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{SecurityElement.Escape(exePath)}</Command><Arguments>--tray</Arguments></Exec></Actions>
</Task>";

            // schtasks needs a file. %TEMP% is writable by every program the user runs, so keep the file open
            // without write/delete sharing while schtasks reads it (nothing can swap its contents), then check
            // that the registered task runs exactly our EXE.
            string tmp = Path.Combine(Path.GetTempPath(), $"pulseram_task_{Guid.NewGuid():N}.xml");
            bool created;
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                {
                    var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                    created = RunSchtasks($"/Create /F /TN \"{ElevatedStartupTaskName}\" /XML \"{tmp}\"") == 0;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not create the elevated startup task", ex);
                created = false;
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }

            if (!created) return false;
            string? registered = GetElevatedTaskCommand();
            if (registered == null || !string.Equals(Path.GetFullPath(registered), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Elevated startup task does not run {exePath} (found '{registered}'); removing it");
                RunSchtasks($"/Delete /TN \"{ElevatedStartupTaskName}\" /F");
                return false;
            }
            return true;
        }

        /// <summary>The EXE the elevated startup task runs, or null if there is no task (or it can't be read).</summary>
        private static string? GetElevatedTaskCommand()
        {
            var (code, output) = RunSchtasksWithOutput($"/Query /XML /TN \"{ElevatedStartupTaskName}\"");
            if (code != 0) return null;
            var m = System.Text.RegularExpressions.Regex.Match(output, "<Command>(.*?)</Command>", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) return null;
            string cmd = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim().Trim('"');
            return cmd.Length == 0 ? null : cmd;
        }

        /// <summary>
        /// An elevated logon task that runs an EXE from a folder ordinary programs can write to (e.g. an older
        /// per-user install, a portable copy, or a folder left behind after uninstalling) would let any program
        /// gain administrator rights at the next logon. Remove it (needs admin); otherwise report it.
        /// </summary>
        public bool UnsafeElevatedTaskFound { get; private set; }

        private void AuditElevatedStartupTask()
        {
            try
            {
                if (!ElevatedStartupTaskExists()) return;
                string? cmd = GetElevatedTaskCommand();
                bool safe = cmd != null && File.Exists(cmd) && InstallSecurity.IsAdminOnlyWritable(cmd);
                if (safe) return;
                if (_isAdmin && RunSchtasks($"/Delete /TN \"{ElevatedStartupTaskName}\" /F") == 0)
                {
                    Log.Warn($"Removed an unsafe elevated startup task (it ran '{cmd}' from a folder other programs can write to)");
                    Config.RunOnStartup = CheckWindowsStartupRegistry();
                    return;
                }
                UnsafeElevatedTaskFound = true;
                Log.Warn($"Unsafe elevated startup task found ('{cmd}'); run PulseRAM as administrator once to remove it");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not check the elevated startup task", ex);
            }
        }

        private static int RunSchtasks(string args) => RunSchtasksWithOutput(args).Code;

        private static (int Code, string Output) RunSchtasksWithOutput(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return (-1, "");
                var errTask = p.StandardError.ReadToEndAsync();
                string output = p.StandardOutput.ReadToEnd();
                errTask.Wait(15000);
                return p.WaitForExit(15000) ? (p.ExitCode, output) : (-1, output);
            }
            catch
            {
                return (-1, "");
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                using var done = new ManualResetEvent(false);
                if (_backgroundTimer != null && _backgroundTimer.Dispose(done))
                    done.WaitOne(3000);
            }
            catch { }
            ThawAllApps();
            _memCounters.Dispose();
        }
    }
}
