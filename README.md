# PulseRAM ⚡

> **Native WinUI 3 + NT Kernel Intelligent Memory Optimizer & Process Governor for Windows 10 & 11**

![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?logo=windows)
![Framework](https://img.shields.io/badge/Framework-WinUI%203%20%7C%20WindowsAppSDK%201.6-4B0082?logo=dotnet)
![Runtime](https://img.shields.io/badge/Runtime-.NET%208.0%20(Self--Contained)-512BD4?logo=dotnet)
![Architecture](https://img.shields.io/badge/Architecture-x64-00599C)
![License](https://img.shields.io/badge/License-MIT-green)

---

## Overview

**PulseRAM** is a pure native Windows desktop application built with **C# (.NET 8.0)**, **WinUI 3 (`Microsoft.WindowsAppSDK 1.6`)**, and direct **Win32 / NT Kernel P/Invoke** (`ntdll.dll`, `psapi.dll`, `kernel32.dll`, `advapi32.dll`).

Unlike web-wrapper or legacy WinForms RAM cleaners, PulseRAM interacts directly with the Windows NT virtual memory manager without requiring third-party drivers or web runtimes.

---

## Key Features

### 1. Memory Reactor Radial Gauge & 60 FPS Interpolation
* **Dual Concentric Arcs**: Real-time visualization of In-Use physical RAM alongside Windows Standby Cache.
* **Smooth Animation Loop**: 16ms (`DispatcherQueueTimer`) LERP interpolation for fluid transitions.
* **XAML Ambient Storyboards**: Breathing glow halo with dynamic pressure-color shifts (Optimal `#10B981`, Elevated `#F59E0B`, Critical `#F43F5E`) and continuous 360° dashed orbital ring rotation.
* **Shockwave Burst**: Triggered on every memory optimization event with spring-ease animations.

### 2. Intelligent Auto-Pilot & Active Guards
* **Configurable Thresholds**: Triggers background optimization when memory load reaches the set percentage (default 80%) or falls below the emergency floor (default 2.0 GB).
* **Foreground App Shield**: Protects the active focused window from being trimmed.
* **Runaway Leak Guardian**: Tracks memory allocation velocity (+MB/min) over rolling 90-second windows and remediates memory leaks before OS freeze.
* **Periodic Maintenance Timer**: Scheduled background sweeps (10m, 15m, 30m, 60m).
* **Auditory Chime Feedback**: Non-intrusive kernel feedback chime on automated sweeps.

### 3. Four Specialized Cleaning Modes
1. **Smart Trim (Safe)**: Empties working sets of idle background processes without touching the active foreground app or system services.
2. **Deep Sweep (Thorough)**: Trims all non-protected processes and triggers memory page combination (`SystemCombinePhysicalMemoryInformation`).
3. **Standby Purge (Kernel Privileged)**: Calls `NtSetSystemInformation(SystemMemoryListInformation)` to purge and flush the Windows Standby List and Modified Page List.
4. **Max Overdrive (Extreme)**: Full sweep across all processes, standby cache purge, and page combination in a single pass.

### 4. Process Inspector & Quick Governor
* Searchable and sortable by **Memory**, **Smart Score**, **CPU %**, or **Leak Velocity**.
* Grouped application view or individual PID view.
* Five instant per-app actions:
  * **Trim**: Instant working set reduction.
  * **Eco**: Enforces Windows EcoQoS power throttling (`PROCESS_POWER_THROTTLING_EXECUTION_SPEED`).
  * **Freeze / Thaw**: Suspends and resumes runaway background processes via `NtSuspendProcess` / `NtResumeProcess`.
  * **Cap**: Sets a hard working-set ceiling in MB.
  * **Shield**: Whitelists applications from all Auto-Pilot sweeps.

### 5. Workload Profiles & Win32 Global Hotkeys
* **Pre-configured Workloads**: Gaming Boost (68% ceiling + game EXE shielding), Creator / Video & 3D (76%), Heavy Dev & AI (82%), and Ultra Low-RAM Guard (65%).
* **Global Hotkeys**:
  * `Ctrl + Shift + M`: Instant Smart Trim anywhere in Windows.
  * `Ctrl + Shift + P`: Instant Deep Sweep + Standby Cache Purge.
* **Win32 System Tray**: Live tooltip memory metrics and quick-action context menu.

---

## Architecture & Kernel APIs

PulseRAM operates directly against official Windows NT subsystem and Win32 APIs:

| Subsystem | API | Purpose |
|---|---|---|
| `kernel32.dll` | `GlobalMemoryStatusEx` | Real-time physical RAM and pagefile metrics |
| `psapi.dll` | `GetPerformanceInfo`, `EmptyWorkingSet` | Working set trimming and kernel commit loads |
| `kernel32.dll` | `SetProcessWorkingSetSizeEx` | Minimum and maximum memory ceiling enforcement |
| `ntdll.dll` | `NtSetSystemInformation` | Standby cache purge and physical memory page combining |
| `ntdll.dll` | `NtSuspendProcess`, `NtResumeProcess` | Process freezing and thawing |
| `kernel32.dll` | `SetProcessInformation` | EcoQoS efficiency mode power throttling |
| `advapi32.dll` | `AdjustTokenPrivileges` | `SeProfileSingleProcessPrivilege` & `SeIncreaseQuotaPrivilege` |

---

## Building from Source

### Prerequisites
* Windows 10 (Build 19041+) or Windows 11
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
* [Inno Setup 6](https://jrsoftware.org/isinfo.php) (for building the installer)

### Build Commands
```powershell
# Clone the repository
git clone https://github.com/Hiirocchi18/PulseRAM.git
cd PulseRAM

# Build Release binaries
dotnet build -c Release -r win-x64 --no-restore

# Build Full Package Installer
.\Build-Installer.ps1
```

The compiled single-file installer will be located in `dist\PulseRAM-Setup-v1.0.0.exe`.

---

## License

Distributed under the MIT License. See [LICENSE](LICENSE) for more information.
