# Changelog

## v1.4.1 — 2026-10-18

Security release. Upgrading is recommended for everyone who used "Start with Windows" while running PulseRAM as administrator.

### Security
- **Fixed: other programs could gain administrator rights through the elevated startup task.**
  - The "start as administrator at logon" task could point to a PulseRAM copy in your user folder, where any program can replace files. It is now only created for all-users installs in Program Files whose files only administrators can change.
  - An existing unsafe task is removed the next time PulseRAM runs as administrator; until then PulseRAM shows a warning.
  - The uninstaller now removes the task too, asking for admin rights if needed.
- The task definition can no longer be swapped while it is being registered. PulseRAM also checks afterwards that the task runs exactly its own EXE.
- When elevated, memory caps, freezing and Eco mode only apply to apps in your own session, never to services or security software. Settings files are written by programs running as you, so they're not trusted to aim an elevated PulseRAM elsewhere.
- When elevated, PulseRAM won't be redirected by junctions, symlinks or hard links planted in its data or log folder.
  - Every write and delete is checked against the real file it opened.
  - Settings are saved atomically.
  - On Windows 11 22H2+, Windows' own *RedirectionGuard* is switched on as well.
  - If its safety record for frozen apps can't be saved, PulseRAM won't freeze anything.
- When elevated, PulseRAM no longer loads DLLs from the current folder.
- Elevated caps, freezing and Eco mode also skip elevated or SYSTEM processes running on your desktop.
- When elevated, PulseRAM opens the release page and the log folder through Explorer, so they don't run as administrator.
- Hardening:
  - The installer calls system tools by full path.
  - Build actions are pinned to exact commits, and the Inno Setup version is pinned.
  - The release tag is passed to scripts as data.
- Updated the Windows App SDK from 1.6 to 1.8 (security and reliability fixes ship inside the app), leaving out its unused AI/ML and Widgets runtimes. The installer is now about 22 MB.

### Other
- Removed the old manual release script, which contained a local folder path; publishing a release on GitHub now does everything.

## v1.4.0 — 2026-10-18

### New
- **Half the download size.** The installer is about 23 MB (22.5 MB for ARM64) instead of 45.6 MB, by leaving out the parts of .NET and the Windows SDK PulseRAM never uses.
- **ARM64 build.** Native installer and portable zip for Windows on ARM (Snapdragon laptops, Surface Pro X …), tested on GitHub's ARM runners.
- **Portable version.** Unzip and run; settings and logs stay in a `data` folder next to the EXE. It falls back to the normal location if that folder isn't writable.
- **Update notice.** Once a day PulseRAM asks GitHub if there's a newer release and shows a banner with *Download* and *Skip this version*. It never downloads anything itself. You can turn it off (*Profiles → Device → Updates*) or *Check now*. This is PulseRAM's only network request.
- **Welcome card** on a new install: the detected PC tier, what Auto-Pilot will do, and opt-ins for *Start with Windows* and update checks. People upgrading from 1.3 don't see it.
- **Possible leaks list** (*Processes* tab). Apps caught growing are remembered with their peak rate, size and start time, and you get one notice per app (at most hourly).
  - *Restart* closes the app normally, so it can save or ask you, then starts it again. It never force-closes, won't touch Windows components, and never hands PulseRAM's admin rights to the restarted app.
  - *Dismiss* hides it until it leaks again later; *Ignore* hides it for good.
- **Minimize to tray** (on by default). The minimize button hides PulseRAM to the system tray, where it keeps running; a one-time notice says where it went. Untick *Profiles → Device → Minimize to tray* for normal taskbar minimizing.
- **Compact widget menu.** Right-click it for the clean mode, Focus Boost, Auto-Pilot, *See-through when idle* (fades to translucent while the mouse is elsewhere), full view and hide.
- **Ready for code signing and winget.**
  - Publishing a GitHub release now builds, tests and attaches every file automatically.
  - Once SignPath Foundation approves the project, releases are also signed: [docs/SIGNING.md](docs/SIGNING.md).
  - winget gets each new version as well: [winget/README.md](winget/README.md).

### Improved
- The installer no longer pre-ticks *Start with Windows* (it matters for silent installs, e.g. through winget).
- Settings saves are serialised, so two quick changes can't lose one.
- **Under the hood:**
  - The 4,000-line window file is split into one file per area.
  - The engine's decision logic (tiers, standby-purge policy, memory-limit warning, back-off, hotkeys, version compare, leak history) moved to `Core/` and is covered by 64 unit tests that run on every push.

## v1.3.1 — 2026-10-18

### Improved
- **A real compact view.** The compact button used to shrink the whole dashboard into a tall, clipped window. It is now a small always-on-top widget (about 300×100): a RAM ring with the %, used/total, free memory and Auto-Pilot status, and one *Clean* button for the selected mode. It has no title bar. Drag it by its body, double-click it (or press ⤢) to return to the full view, or hide it to the tray. It remembers where you put it and starts in the top-right corner the first time.
- While compact, PulseRAM refreshes only the numbers the widget shows (no process list, no gauge animation), so it uses less CPU.
- New `--compact` launch option opens straight into the widget.

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
