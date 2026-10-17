# Changelog

## v1.3.0 — 2026-10-18

### New
- **Memory-limit warning.** What actually crashes apps ("out of memory") is the commit limit — RAM plus page file — not RAM %. When committed memory passes 90% (critical at 96%), PulseRAM shows a banner, or a Windows notification while it's in the tray, with advice for your setup: no page file, a page file capped too low, or which app is using the most. One click opens the page-file settings. Toggle: *Memory warnings*.
- **Light theme.** PulseRAM follows the Windows light/dark setting and switches live; pick Dark or Light under *Profiles → Device* to override.
- **Remembers its window.** Size, position and maximized state are restored on the next launch (re-centred if that monitor is no longer connected), and the window has a sensible minimum size.
- **Crash log.** Errors used to be silently swallowed. They are now written to `%LocalAppData%\PulseRAM\logs` along with a startup line and every sweep; PulseRAM tells you when something went wrong or the last session ended unexpectedly. *Logs* button on the Device card.

### Improved
- Clicking a PulseRAM notification opens the dashboard.
- The first window size fits small laptop screens instead of assuming 1440×930.

## v1.2.0 — 2026-10-18

Ideas borrowed (as designs, not code) from MIT-licensed [System Informer](https://github.com/winsiderss/systeminformer), [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) and [tray-monitor](https://github.com/strayge/tray-monitor).

### New
- **Live RAM % in the tray icon.** The icon shows the current memory load and turns amber/red under pressure — readable at a glance without opening PulseRAM. Drawn natively for every display scaling (16–32 px). Toggle: *Profiles → Device → Tray %*.
- **Efficiency Mode while hidden.** PulseRAM runs itself with EcoQoS and below-normal priority while it sits in the tray, and returns to normal when you open it. Toggle: *Eco in tray*.
- **Smart standby purge.** Automatic sweeps (Auto-Pilot, schedule, profile launch) now empty the standby cache only when it would help: free memory is low, or Windows is already evicting cached pages. Purging a healthy cache just makes apps reload from disk. Manual sweeps still always purge. Toggle: *Smart purge*.
- **See the standby cache.** Hover *Standby* on the dashboard for the cache split by priority, the live eviction rate (as administrator), and whether a purge would help right now.
- **Page combining is measured.** Deep and Max sweeps report how much memory deduplicating identical pages saved.

### Notes
- The standby-by-priority breakdown and eviction rate need administrator rights (the same kernel query System Informer uses); without them the free/standby totals still come from Windows' performance counters.

## v1.1.0 — 2026-10-17

A reliability and honesty release: Auto-Pilot no longer fights low-RAM PCs, nothing can stay frozen by accident, every number shown is measured, and the app tunes itself to the PC it runs on.

### Works on any PC
- **Device-aware defaults.** On first run PulseRAM detects RAM and CPU threads and picks a tier (low-end / mid-range / high-end) with a matching Auto-Pilot trigger, free-RAM floor, cooldown and profile triggers. **Re-tune** (Profiles → Device) restores them later.
- **Lite UI** for low-end PCs (automatic, or pick it): no ambient animations, slower refresh, half as many process scans, fewer rows. Animations also respect the Windows *Animation effects* setting.

### Fixes
- **Auto-Pilot no longer trims in a loop.** v1.0 swept every 30 s whenever free RAM was under 2 GB — constantly on 8 GB PCs. The cooldown now backs off (up to 30 min) while sweeps can't relieve pressure, and sweeps pause while the CPU is busy.
- **Smart Trim really is "idle apps only"** (it used to trim every process over 25 MB). Max Overdrive no longer uses the system-wide trim that ignored shields.
- **Frozen apps can't get stuck.** Every frozen process is journaled, resumed automatically after a crash, kill, upgrade or uninstall, and freeze/thaw can no longer suspend a process twice.
- **Memory caps are real.** Caps are now hard per-process limits applied once, instead of a hint that was re-trimmed every 1.5 s.
- **System processes are actually protected.** Name matching for Registry, Memory Compression and spoolsv was broken.
- **Freeze and Eco now trim** (they lacked the access right, so the trim silently failed).
- **Honest numbers.** v1.0 reported invented "freed" values (4.2 MB × processes, a 2 MB minimum, a 44% "reclaimable" estimate). Results now show the working set actually trimmed and the measured change in available/free memory; the Standby figure comes from Windows' own memory-list counters.
- **Leak guard** watches private memory (what really grows in a leak) with a sustained-growth check and a 5-minute per-app cooldown.
- **Settings save reliably** to `%LocalAppData%\PulseRAM` (v1.0 settings are imported once). The shipped defaults file now matches what the code reads — v1.0's was silently ignored.
- **Hotkeys no longer clash** with Teams (Ctrl+Shift+M) or VS Code (Ctrl+Shift+P): new defaults are Ctrl+Alt+Shift+M / P, they're configurable, and conflicts are reported.
- **Opening PulseRAM again** brings the existing window forward instead of doing nothing; restarting as admin no longer races itself; the tray icon comes back after Explorer restarts.
- **Starts elevated at logon** (Task Scheduler) when enabled while running as admin, so standby purges keep working after a reboot.
- **Lighter in the tray:** no UI rendering or animation while hidden; process scans never run on the UI thread.
- **Installer:** correct publisher and links, asks PulseRAM to exit cleanly (thawing frozen apps) before upgrading or uninstalling, removes the elevated startup task on uninstall.

### UI
- Minimal layout: compact top bar, short labels, explanations moved into tooltips.

### Under the hood
- .NET 10 (LTS) — .NET 8 support ends 10 Nov 2026.
- System CPU from `GetSystemTimes`; thread-safe engine state; per-process history pruned.
- GitHub Actions build with a launch smoke test and a silent install → launch → uninstall test.

### Upgrade notes
- Your v1.0 settings are imported automatically. Profile EXEs that v1.0 permanently added to *Shielded* are removed (profiles now shield them only while active).
- Existing memory caps become hard per-process limits.
- Hotkeys still set to the old Ctrl+Shift+M / P defaults move to Ctrl+Alt+Shift+M / P.

## v1.0.0
- First release.
