# PulseRAM ⚡

> **Intelligent Memory Optimizer & Process Governor for Windows 10 & 11**

![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?logo=windows)
![Framework](https://img.shields.io/badge/Framework-WinUI%203%20%7C%20WindowsAppSDK%201.6-4B0082?logo=dotnet)
![Runtime](https://img.shields.io/badge/Runtime-.NET%2010%20(Self--Contained)-512BD4?logo=dotnet)
![Architecture](https://img.shields.io/badge/Architecture-x64-00599C)
![License](https://img.shields.io/badge/License-MIT-green)

---

## Overview

**PulseRAM** is a pure native Windows desktop application built with **C# (.NET 10)**, **WinUI 3 (`Microsoft.WindowsAppSDK 1.6`)**, and direct **Win32 / NT Kernel P/Invoke** (`ntdll.dll`, `psapi.dll`, `kernel32.dll`, `advapi32.dll`, `pdh.dll`).

Unlike web-wrapper or legacy WinForms RAM cleaners, PulseRAM interacts directly with the Windows NT virtual memory manager without requiring third-party drivers or web runtimes. It makes **no network connections** and collects no telemetry.

---

## Built for any PC

PulseRAM detects your hardware on first run and tunes itself — no setup needed on either a 4 GB laptop or a 64 GB workstation:

| Tier | Detected as | Auto-Pilot trigger | Free-RAM floor | Cooldown | UI |
|---|---|---|---|---|---|
| **Low-end** | < 7 GB RAM or ≤ 2 threads | 88% | 0.5 GB | 5 min | Lite |
| **Mid-range** | < 15 GB RAM or ≤ 4 threads | 85% | 0.75 GB | 3 min | Full |
| **High-end** | 16 GB+ | 80% | 1.5 GB | 2 min | Full |

Low-RAM PCs normally run at a high memory load, so they get a higher trigger and a longer cooldown — otherwise Auto-Pilot would trim all the time and make the PC slower. Profile triggers are raised by the same margin.

**Lite UI** (automatic on low-end PCs, or pick it under *Profiles → Device*) turns off ambient animations, refreshes every 3 s instead of 1.5 s, scans processes half as often, and shows fewer rows. Animations also switch off when Windows *Animation effects* is disabled. **Re-tune** on the same card restores the recommended values for your PC.

---

## What RAM trimming does (and doesn't)

Windows already manages memory well. Trimming a working set does **not** delete anything: it moves an app's pages out of physical RAM onto the standby/modified lists, and the app faults them back in the next time it touches them. That is useful when a few idle apps are crowding out the one you're using, and harmful if done constantly. PulseRAM is designed around that:

* **Smart Trim only touches idle apps**, never the window you're using, and never shielded or system processes.
* **Auto-Pilot backs off** (cooldown doubles, up to 30 min) when a sweep can't relieve pressure, instead of trimming in a loop.
* **Every number is measured.** Reported results are the working-set MB actually paged out and the measured change in Available / Free memory — no estimates.

---

## Key Features

The interface is deliberately minimal: short labels, with details in hover tooltips (mode cards, guard tiles, status chips, profiles).

### 1. Memory Reactor Radial Gauge & 60 FPS Interpolation
* **Dual Concentric Arcs**: In-use physical RAM alongside the real Windows **Standby list** size (read from the built-in `Memory` performance counters).
* **Smooth Animation Loop**: 16ms (`DispatcherQueueTimer`) LERP interpolation for fluid transitions — paused automatically while PulseRAM is hidden in the tray.
* **XAML Ambient Storyboards**: Breathing glow halo with dynamic pressure-color shifts (Optimal `#10B981`, Elevated `#F59E0B`, Critical `#F43F5E`) and continuous 360° dashed orbital ring rotation.
* **Shockwave Burst**: Triggered on every memory optimization event with spring-ease animations.

### 2. Intelligent Auto-Pilot & Active Guards
* **Configurable Thresholds**: Sweeps when memory load reaches the set percentage or Available RAM falls below the floor (defaults depend on your PC — see *Built for any PC*).
* **Adaptive Cooldown**: A minimum gap between sweeps that doubles (up to 30 min) while sweeps fail to bring memory back under the trigger, and resets once pressure clears.
* **CPU Guard**: Skips automatic sweeps while the system is busy (default ≥ 75% CPU), when extra page faults would hurt most.
* **Foreground App Shield**: Never trims the active focused window.
* **Runaway Leak Guardian**: Watches **private memory** (what grows when an app leaks) for sustained growth (default +120 MB/min over a rolling 2-minute window). It flags the app and trims its working set at most every 5 minutes. Trimming relieves physical RAM but can't release leaked memory — restart the app for that.
* **Periodic Maintenance Timer**: Scheduled background sweeps (10m, 15m, 30m, 60m).
* **Auditory Chime Feedback**: Optional chime on automated sweeps.

### 3. Four Cleaning Modes

| Mode | What it does | Needs admin |
|---|---|---|
| **Smart Trim** (Safe) | Empties the working sets of **idle** background processes (CPU ≤ 2.5%, above a size that depends on your PC). | No |
| **Deep Sweep** (Thorough) | Trims every unshielded process. As admin, also combines identical pages, flushes the modified list, and purges the **low-priority** standby list. | For the kernel part |
| **Standby Purge** | Flushes the modified list and purges the whole standby list. Does nothing (and says so) without admin. | Yes |
| **Max Overdrive** (Extreme) | Deep trim + page combining + modified flush + **full** standby purge. | For the kernel part |

Shielded apps, critical system processes and (by default) the foreground app are skipped in **every** mode.

### 4. Process Inspector & Quick Governor
* Searchable and sortable by **Memory**, **Smart Score**, **CPU %**, or **Leak Velocity**.
* Grouped application view or individual PID view.
* Five instant per-app actions:
  * **Trim**: Instant working set reduction (shows the MB actually paged out).
  * **Eco**: Enforces Windows EcoQoS power throttling (`PROCESS_POWER_THROTTLING_EXECUTION_SPEED`) and idle priority.
  * **Freeze / Thaw**: Suspends and resumes background processes via `NtSuspendProcess` / `NtResumeProcess`. Every frozen PID is journaled to disk, so apps are automatically resumed if PulseRAM crashes, is killed, upgraded or uninstalled.
  * **Cap**: Sets a **hard per-process** working-set ceiling in MB (`QUOTA_LIMITS_HARDWS_MAX_ENABLE`). Windows enforces it continuously; PulseRAM checks once a minute and applies it only to new processes of the app. A cap that's too low makes the app page heavily.
  * **Shield**: Whitelists applications from all Auto-Pilot sweeps.

### 5. Workload Profiles, Global Hotkeys & Tray
* **Pre-configured Workloads**: Gaming Boost (68%), Creator & 3D Studio (72%), Dev & Local AI (76%), and Browser Tab Tamer (78%). A matched profile temporarily shields its running EXEs while it is active.
* **Global Hotkeys** (configurable via `HotkeyPrimary` / `HotkeySecondary` in `%LocalAppData%\PulseRAM\pulseram_config.json`, e.g. `"Ctrl+Alt+Shift+M"`, `"Ctrl+Win+F9"`):
  * `Ctrl + Alt + Shift + M`: Smart Trim of idle background apps.
  * `Ctrl + Alt + Shift + P`: Deep Sweep.
  * The defaults avoid `Ctrl+Shift+M` (Teams mute) and `Ctrl+Shift+P` (VS Code command palette). If a hotkey is already taken, PulseRAM tells you instead of failing silently.
* **Win32 System Tray**: Live tooltip memory metrics and quick-action context menu. Launching PulseRAM again while it's in the tray brings the existing window forward.

### 6. Windows Startup
* Toggle **Windows Startup** on the dashboard. When PulseRAM is running **as administrator**, startup is registered as an elevated Task Scheduler task (`PulseRAM Elevated Startup`) so Standby/Deep/Max kernel purges keep working after a reboot. Otherwise it uses the per-user `HKCU\...\Run` key and starts as a standard user.

---

## Settings & Data

| File | Location | Purpose |
|---|---|---|
| `pulseram_config.json` | `%LocalAppData%\PulseRAM\` | Your settings (always writable, even for an all-users install). |
| `frozen_processes.json` | `%LocalAppData%\PulseRAM\` | Journal of PIDs PulseRAM has frozen; used to resume them after a crash. |
| `pulseram_defaults.json` | Install folder | Defaults applied on first run. Settings saved by v1.0 next to the EXE are imported once. |

Command-line switches: `--tray` (start hidden in the tray), `--thaw-frozen` (resume anything PulseRAM left suspended, then exit; used by the installer).

---

## Architecture & Kernel APIs

PulseRAM operates directly against official Windows NT subsystem and Win32 APIs:

| Subsystem | API | Purpose |
|---|---|---|
| `kernel32.dll` | `GlobalMemoryStatusEx` | Real-time physical RAM and pagefile metrics |
| `pdh.dll` | `PdhAddEnglishCounter` (`\Memory\Standby Cache *`, `\Memory\Free & Zero Page List Bytes`) | Real Standby and Free list sizes (no admin needed) |
| `psapi.dll` | `GetPerformanceInfo`, `EmptyWorkingSet` | Working set trimming and kernel commit loads |
| `kernel32.dll` | `SetProcessWorkingSetSizeEx` | Hard per-app working-set ceilings |
| `ntdll.dll` | `NtSetSystemInformation` | Standby cache purge, modified flush and physical memory page combining |
| `ntdll.dll` | `NtSuspendProcess`, `NtResumeProcess` | Process freezing and thawing |
| `kernel32.dll` | `SetProcessInformation` | EcoQoS efficiency mode power throttling |
| `advapi32.dll` | `AdjustTokenPrivileges` | `SeProfileSingleProcessPrivilege` & `SeIncreaseQuotaPrivilege` |

---

## Building from Source

### Prerequisites
* Windows 10 (Build 19041+) or Windows 11
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
* [Inno Setup 6](https://jrsoftware.org/isinfo.php) (for building the installer)

### Build Commands
```powershell
# Clone the repository
git clone https://github.com/Hiirocchi18/PulseRAM.git
cd PulseRAM

# Build Release binaries
dotnet build -c Release -r win-x64

# Build Full Package Installer
.\Build-Installer.ps1
```

The compiled single-file installer will be located in `dist\PulseRAM-Setup-v1.1.0.exe`.

Every push is also built on GitHub Actions (`.github/workflows/build.yml`): publish, resource check, a launch smoke test, the Inno Setup installer, and a silent install → launch → uninstall test. The installer is attached to the run as an artifact.

> **Note:** the installer and binaries are not code-signed, so Windows SmartScreen shows a warning on first run. Signing with a code-signing certificate removes it.

---

## License

Distributed under the MIT License. See [LICENSE](LICENSE) for more information.
