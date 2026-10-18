using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace PulseRAM
{
    public sealed partial class MainWindow
    {
        #region Live UI Rendering & Interactive Actions

        private void SwitchView(string viewName)
        {
            _currentView = viewName;
            _viewOverview.Visibility = viewName == "overview" ? Visibility.Visible : Visibility.Collapsed;
            _viewProcesses.Visibility = viewName == "processes" ? Visibility.Visible : Visibility.Collapsed;
            _viewProfiles.Visibility = viewName == "profiles" ? Visibility.Visible : Visibility.Collapsed;

            UIElement activeView = viewName switch
            {
                "processes" => _viewProcesses,
                "profiles" => _viewProfiles,
                _ => _viewOverview
            };
            AnimateViewEntrance(activeView);

            StyleNavTab(_tabBtnOverview, viewName == "overview");
            StyleNavTab(_tabBtnProcesses, viewName == "processes");
            StyleNavTab(_tabBtnProfiles, viewName == "profiles");

            RefreshAllUi();
        }

        private static void StyleNavTab(Button btn, bool active)
        {
            btn.Background = active ? BrushHex("#38BDF8", 45) : new SolidColorBrush(Colors.Transparent);
            btn.BorderBrush = active ? BrushHex("#38BDF8", 120) : new SolidColorBrush(Colors.Transparent);
            btn.BorderThickness = new Thickness(1);
        }

        private void SelectCleaningMode(string modeKey)
        {
            _selectedMode = modeKey;
            foreach (var kv in _modeCards)
            {
                bool active = kv.Key == modeKey;
                kv.Value.Background = active ? BrushHex("#0EA5E9", 42) : BrushHex("#151F35", 150);
                kv.Value.BorderBrush = active ? BrushHex("#38BDF8") : BrushHex("#94A3B8", 35);
            }

            string title = modeKey switch
            {
                "smart" => "⚡ Clean · Smart",
                "deep" => "⚡ Clean · Deep",
                "standby" => "⚡ Clean · Standby",
                "max" => "⚡ Clean · Max",
                _ => "⚡ Clean"
            };
            _optimizeBtnLabel.Text = title;
        }

        private void SetProcessGrouping(bool grouped)
        {
            _groupedProcesses = grouped;
            _btnGroupApps.Background = grouped ? BrushHex("#38BDF8", 45) : BrushHex("#151F35", 180);
            _btnGroupApps.BorderBrush = grouped ? BrushHex("#38BDF8") : BrushHex("#94A3B8", 40);
            _btnGroupPids.Background = !grouped ? BrushHex("#38BDF8", 45) : BrushHex("#151F35", 180);
            _btnGroupPids.BorderBrush = !grouped ? BrushHex("#38BDF8") : BrushHex("#94A3B8", 40);
            RenderProcessLists(_engine.GetProcesses(_groupedProcesses));
        }

        private void ToggleFocusBoost()
        {
            _engine.Config.FocusBoostActive = !_engine.Config.FocusBoostActive;
            _engine.SaveConfig();
            if (_engine.Config.FocusBoostActive)
            {
                RunOptimizationAsync("smart", "Focus Boost Overdrive Activated");
            }
            else
            {
                RefreshAllUi();
                ShowToast("Focus Boost Deactivated", "Returned Auto-Pilot to normal profile thresholds.");
            }
        }

        private void ThawAllFrozenApps()
        {
            RunEngineActionAsync(() => _engine.ThawAllApps(), count =>
                ShowToast("Thawed All Processes", $"Resumed {count} suspended application(s)."), "thaw-all");
        }

        private readonly HashSet<string> _pendingActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Runs an engine call (process enumeration, OpenProcess…) off the UI thread, then refreshes.
        /// Repeat clicks for the same <paramref name="key"/> are ignored until the first one finishes,
        /// and exceptions are reported instead of crashing the process (ThreadPool exceptions are fatal).
        /// </summary>
        private void RunEngineActionAsync<T>(Func<T> work, Action<T> onDone, string? key = null, Action? always = null)
        {
            if (key != null && !_pendingActions.Add(key)) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                T result = default!;
                Exception? error = null;
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (key != null) _pendingActions.Remove(key);
                    always?.Invoke();
                    RefreshAllUi();
                    if (error != null) ShowToast("Action failed", error.Message);
                    else onDone(result);
                });
            });
        }

        private void RequestAdminElevation()
        {
            if (_engine.GetSnapshot().IsAdmin)
            {
                ShowToast("Kernel Admin Active", "SeProfileSingleProcessPrivilege & Standby List Purge are unlocked.");
                return;
            }
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PulseRAM.exe");
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    // Tells the new instance to wait for this one to exit instead of bowing out
                    // because the single-instance mutex is still held.
                    Arguments = "--elevated-relaunch",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                Close();
            }
            catch
            {
                ShowToast("Elevation Cancelled", "Continuing in standard user working-set trim mode.");
            }
        }

        private void RunOptimizationAsync(string mode, string reason)
        {
            _optimizeNowBtn.IsEnabled = false;
            _optimizeBtnLabel.Text = "Cleaning…";
            _miniCleanBtn.IsEnabled = false;
            _miniCleanLabel.Text = "Cleaning…";
            TriggerReactorShockwaveAnimation();

            RunEngineActionAsync(() => _engine.OptimizeMemory(mode, "Manual", reason), ev =>
            {
                TriggerReactorShockwaveAnimation();
                var (title, sub) = DescribeEvent(ev);
                ShowToast(ev.Skipped ? title : $"{title} in {ev.DurationMs} ms", sub);
            }, "sweep", always: () =>
            {
                _optimizeNowBtn.IsEnabled = true;
                _miniCleanBtn.IsEnabled = true;
                SelectCleaningMode(_selectedMode);
                if (_isMiniHud) RefreshAllUi(force: true);
            });
        }

        private void OnEngineOptimizationCompleted(OptimizationEvent ev)
        {
            // Manual sweeps toast from RunOptimizationAsync; per-app buttons toast themselves.
            if (ev.Trigger != "Manual" && ev.Trigger != "Targeted")
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    TriggerReactorShockwaveAnimation();
                    RefreshAllUi();
                    var (title, sub) = DescribeEvent(ev);
                    ShowToast($"{ev.Trigger}: {title}", $"{ev.Reason} · {sub}");
                });
            }
            else if (ev.Trigger == "Targeted")
            {
                DispatcherQueue.TryEnqueue(() => RefreshAllUi());
            }
        }

        /// <summary>Standby cache explained: per-priority sizes, eviction rate, and whether a purge would help.</summary>
        private string BuildStandbyTooltip(MemorySnapshot snap)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Cached files Windows can drop instantly when an app needs RAM.");
            if (snap.StandbyByPriorityGb is { Length: 8 } p)
            {
                sb.Append("\n\nBy priority (0 = least important):");
                sb.Append($"\n0–1: {p[0] + p[1]:F2} GB · 2–4: {p[2] + p[3] + p[4]:F2} GB · 5–7: {p[5] + p[6] + p[7]:F2} GB");
                if (snap.RepurposeMbPerMin >= 0)
                    sb.Append($"\nBeing evicted: {snap.RepurposeMbPerMin:0} MB/min");
            }
            else
            {
                sb.Append("\n\nRun as Admin to see it by priority.");
            }
            if (!snap.StandbyPurgeSupported)
                sb.Append("\n\nPurging the cache needs Admin.");
            else if (snap.StandbyPurgeWorthwhile)
                sb.Append("\n\nFree memory is low — a purge would help.");
            else if (!_engine.Config.SmartStandbyPurge)
                sb.Append("\n\nSmart purge is off — automatic sweeps purge anyway.");
            else if (snap.StandbyAndCacheGb < Math.Max(0.5, snap.TotalGb * 0.10))
                sb.Append("\n\nThe cache is small — Auto-Pilot leaves it alone.");
            else
                sb.Append("\n\nFree memory is fine — Auto-Pilot leaves the cache alone.");
            return sb.ToString();
        }

        /// <summary>Describes an optimization using only measured values.</summary>
        private static (string Title, string Sub) DescribeEvent(OptimizationEvent ev)
        {
            if (ev.Skipped)
                return ($"{ev.Mode}: nothing changed", string.IsNullOrEmpty(ev.Note) ? ev.Reason : ev.Note);

            string title = ev.Mode == "STANDBY"
                ? (ev.StandbyPurged
                    ? $"Standby cache purged · free memory +{FormatMb(ev.FreeListGainMb)}"
                    : "Standby purge failed")
                : $"Trimmed {FormatMb(ev.FreedMb)} from {ev.ProcessesTrimmed} process(es)";

            var parts = new List<string>();
            if (ev.Mode != "STANDBY") parts.Add($"Available +{FormatMb(ev.AvailableGainMb)}");
            parts.Add($"RAM {ev.BeforePercent:F1}% → {ev.AfterPercent:F1}%");
            if (ev.StandbyPurged && ev.Mode != "STANDBY") parts.Add("standby purged");
            if (ev.CombinedMb >= 1) parts.Add($"{FormatMb(ev.CombinedMb)} deduplicated");
            if (!string.IsNullOrEmpty(ev.Note)) parts.Add(ev.Note);
            return (title, string.Join(" · ", parts));
        }

        private void ShowToast(string title, string sub)
        {
            if (_isMiniHud)
            {
                // The widget has no room for a banner: show the headline on its status line for a few seconds.
                _miniStatusText.Text = title;
                _miniStatusText.Foreground = BrushHex("#38BDF8");
                ToolTipService.SetToolTip(_miniStatusText, sub);
                _miniStatusUntil = DateTime.Now.AddSeconds(6);
            }
            _toastTitleText.Text = title;
            _toastSubText.Text = sub;
            _toastBanner.Visibility = Visibility.Visible;

            try
            {
                _toastBanner.RenderTransform = new CompositeTransform { TranslateY = 36 };
                var sb = new Storyboard();
                var slide = new DoubleAnimation
                {
                    From = 36.0,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(320)),
                    EnableDependentAnimation = true,
                    EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(slide, _toastBanner);
                Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");

                var fade = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EnableDependentAnimation = true
                };
                Storyboard.SetTarget(fade, _toastBanner);
                Storyboard.SetTargetProperty(fade, "Opacity");

                sb.Children.Add(slide);
                sb.Children.Add(fade);
                sb.Begin();
            }
            catch { }

            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private static string FormatMb(double mb)
        {
            return mb >= 1024.0 ? $"{(mb / 1024.0):F2} GB" : $"{Math.Round(mb)} MB";
        }

        private bool IsDashboardVisible() => IsWindowVisible(_hwnd) && !IsIconic(_hwnd);

        private static bool WindowsAnimationsEnabled()
        {
            try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
            catch { return true; }
        }

        /// <summary>
        /// Lite mode (auto on low-end PCs): slower refresh, no ambient animation, fewer process rows.
        /// Reduce-motion also follows the Windows "Animation effects" accessibility setting.
        /// </summary>
        private void ApplyPerformanceMode()
        {
            bool lite = _engine.LiteMode;
            _reduceMotion = lite || !WindowsAnimationsEnabled();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(lite ? 3000 : 1500);
            if (!_reduceMotion && _oldWndProc != IntPtr.Zero) StartAmbientXamlAnimations(); // switched to Full at runtime
            _animationsPaused = !_animationsPaused; // force SetAnimationsPaused to re-evaluate
            SetAnimationsPaused(_reduceMotion || !IsDashboardVisible());
            if (_reduceMotion) StepGauge(1.0);
        }

        /// <summary>Pauses the 60 FPS gauge timer and looping storyboards while the window is hidden or minimised.</summary>
        private void SetAnimationsPaused(bool paused)
        {
            if (paused == _animationsPaused) return;
            _animationsPaused = paused;
            try
            {
                if (_gaugeAnimTimer != null)
                {
                    if (paused) _gaugeAnimTimer.Stop();
                    else _gaugeAnimTimer.Start();
                }
                foreach (var sb in new[] { _reactorBreathingStoryboard, _reactorOrbitalStoryboard, _logoPulseStoryboard })
                {
                    if (sb == null) continue;
                    if (paused) sb.Pause();
                    else sb.Resume();
                }
            }
            catch { }
        }

        private void RefreshAllUi(bool force = false)
        {
            if (_isUpdatingUi) return;

            // While PulseRAM sits in the tray, only the tooltip needs updating: rebuilding ~130 process rows
            // and animating the gauge every 1.5 s for an invisible window just burns CPU and battery.
            if (!force && !IsDashboardVisible())
            {
                var hiddenSnap = _engine.GetSnapshot();
                UpdateTrayTooltip(hiddenSnap);
                SetAnimationsPaused(true);
                SetSelfEfficiencyMode(_engine.Config.EfficiencyModeWhenHidden);
                return;
            }
            if (_isMiniHud)
            {
                // The widget only needs one snapshot; skip the process list and the gauge animation.
                var miniSnap = _engine.GetSnapshot();
                UpdateTrayTooltip(miniSnap);
                SetSelfEfficiencyMode(false);
                SetAnimationsPaused(true);
                UpdateMiniHud(miniSnap);
                return;
            }
            SetAnimationsPaused(_reduceMotion);

            _isUpdatingUi = true;
            try
            {
                var snap = _engine.GetSnapshot();
                var procs = _engine.GetProcesses(_groupedProcesses);
                var events = _engine.GetEvents();
                var telemetry = _engine.GetTelemetry();

                UpdateTrayTooltip(snap);
                RenderLeakList();
                SetSelfEfficiencyMode(false);

                // Top Command Header
                _clockText.Text = DateTime.Now.ToString("HH:mm:ss");
                _tabProcCountText.Text = procs.Count.ToString();
                _sessionFreedTopText.Text = FormatMb(snap.SessionFreedMb);

                _focusBoostBtn.Content = _engine.Config.FocusBoostActive ? "⚡ Focus On" : "⚡ Focus";
                _focusBoostBtn.BorderBrush = _engine.Config.FocusBoostActive ? BrushHex("#A855F7") : BrushHex("#94A3B8", 45);
                _focusBoostBtn.Background = _engine.Config.FocusBoostActive ? BrushHex("#A855F7", 45) : BrushHex("#151F35", 190);

                _apStatusDot.Fill = _engine.Config.AutoEnabled ? BrushHex("#10B981") : BrushHex("#64748B");
                _apStatusPillText.Text = _engine.Config.AutoEnabled ? $"Auto {snap.EffectiveThresholdPercent}%" : "Auto off";
                string apTip = _engine.Config.AutoEnabled
                    ? $"Auto-Pilot sweeps at {snap.EffectiveThresholdPercent}% RAM · profile: {snap.ActiveProfileName} · cooldown {snap.EffectiveCooldownSeconds / 60.0:0.#} min"
                    : "Auto-Pilot is paused";
                if (!Equals(_apStatusTip.Content, apTip)) _apStatusTip.Content = apTip;

                if (snap.IsAdmin)
                {
                    _adminBtn.Content = "✓ Admin";
                    const string adminTip = "Running as administrator: kernel purges available.";
                    if (!Equals(_adminTip.Content, adminTip)) _adminTip.Content = adminTip;
                    _adminBtn.Background = BrushHex("#10B981", 35);
                    _adminBtn.BorderBrush = BrushHex("#10B981", 110);
                    _adminBtn.Foreground = BrushHex("#34D399");
                }

                // Memory Reactor Gauge Targets (interpolated at 60 FPS by _gaugeAnimTimer)
                _targetPercent = snap.Percent;
                _gaugeSubText.Text = $"{snap.UsedGb:F2} / {snap.TotalGb:F2} GB";

                string stateHex = snap.PressureLevel == "CRITICAL" ? "#F43F5E"
                    : (snap.PressureLevel == "ELEVATED" ? "#F59E0B" : "#10B981");
                _pressureBadgeText.Text = $"{snap.PressureLevel} LOAD";
                _pressureBadgeText.Foreground = BrushHex(stateHex);
                _pressureBadgeBorder.BorderBrush = BrushHex(stateHex, 120);
                _pressureBadgeBorder.Background = BrushHex(stateHex, 35);
                _gaugeUsedPath.Stroke = BrushHex(stateHex);
                _reactorPulseHalo.Stroke = BrushHex(stateHex, 115);

                double usedRatio = Math.Clamp(snap.Percent / 100.0, 0.01, 0.999);
                // Standby is part of "Available", so used + standby + free ≈ total.
                double cacheRatio = Math.Clamp(snap.StandbyAndCacheGb / Math.Max(0.1, snap.TotalGb), 0.0, Math.Max(0.0, 0.98 - usedRatio));
                double combinedRatio = Math.Clamp(usedRatio + cacheRatio, 0.01, 0.999);

                _targetUsedRatio = usedRatio;
                _targetCombinedRatio = combinedRatio;
                _targetThresholdPct = snap.EffectiveThresholdPercent;
                if (_reduceMotion) StepGauge(1.0);

                // Physical Memory Map
                _availSummaryText.Text = $"{snap.AvailableGb:F2} GB Available";
                double freeRatio = Math.Max(0.02, 1.0 - usedRatio - cacheRatio);
                _colSegUsed.Width = new GridLength(Math.Max(2, usedRatio * 100), GridUnitType.Star);
                _colSegCache.Width = new GridLength(Math.Max(2, cacheRatio * 100), GridUnitType.Star);
                _colSegFree.Width = new GridLength(Math.Max(2, freeRatio * 100), GridUnitType.Star);
                _legUsedText.Text = $"{snap.UsedGb:F2} GB";
                _legCacheText.Text = snap.MemoryListsMeasured ? $"{snap.StandbyAndCacheGb:F2} GB" : $"~{snap.StandbyAndCacheGb:F2} GB (est.)";
                string standbyTip = BuildStandbyTooltip(snap);
                if (!Equals(_standbyTip.Content, standbyTip)) _standbyTip.Content = standbyTip;
                _legAvailText.Text = $"{snap.AvailableGb:F2} GB";

                if (_selectedMode == "standby")
                {
                    _optimizeBtnSub.Text = snap.StandbyPurgeSupported
                        ? $"{snap.StandbyAndCacheGb:F2} GB standby"
                        : "needs Admin";
                }
                else
                {
                    double trimmableMb = procs.Sum(p => p.ReclaimableMb);
                    _optimizeBtnSub.Text = $"up to {FormatMb(trimmableMb)} trimmable";
                }

                // 6 Kernel Vitals
                _vitReclaimedVal.Text = FormatMb(snap.SessionFreedMb);
                _vitReclaimedSub.Text = $"{snap.OptimizationCount} sweeps";

                _vitCommitVal.Text = $"{snap.CommitTotalGb:F1} / {snap.CommitLimitGb:F1} GB";
                _vitCommitSub.Text = snap.CommitLevel == "ok" ? $"{snap.CommitPercent:F0}% used" : $"{snap.CommitPercent:F0}% used — near the limit";
                _vitCommitVal.Foreground = BrushHex(snap.CommitLevel == "critical" ? "#F43F5E" : (snap.CommitLevel == "high" ? "#F59E0B" : "#F8FAFC"));
                if (snap.CommitLevel == "ok" && _commitBanner.Visibility == Visibility.Visible)
                {
                    _commitBanner.Visibility = Visibility.Collapsed;
                    _commitAdvice = null;
                }

                _vitPagefileVal.Text = snap.PagefileTotalGb > 0.05 ? $"{snap.PagefileUsedGb:F2} GB" : "0.00 GB";
                _vitPagefileSub.Text = snap.PagefileTotalGb > 0.05
                    ? $"{snap.PagefilePercent:F0}% of {snap.PagefileTotalGb:F1} GB"
                    : "idle";

                _vitKernelVal.Text = $"{snap.KernelTotalMb:F0} MB";
                _vitKernelSub.Text = $"paged {snap.KernelPagedMb:F0} · non-paged {snap.KernelNonPagedMb:F0} MB";

                _vitCpuVal.Text = $"{snap.CpuPercent:F1}%";
                string fgShort = string.IsNullOrEmpty(snap.ForegroundApp) ? "Desktop" : snap.ForegroundApp;
                _vitCpuSub.Text = $"{snap.ProcessCount} processes";

                int frozenCnt = procs.Count(p => p.IsFrozen);
                int ecoCnt = procs.Count(p => p.IsEco);
                int capCnt = _engine.Config.AppMemoryCapsMb?.Count ?? 0;
                int protCnt = _engine.Config.ProtectedApps?.Count ?? 0;
                _vitGovVal.Text = $"{protCnt} shield · {capCnt} cap";
                _vitGovSub.Text = $"{frozenCnt} frozen · {ecoCnt} eco";

                // Scheduled Countdown
                if (_engine.Config.ScheduledTrimEnabled && snap.NextScheduledSeconds > 0)
                {
                    int mm = snap.NextScheduledSeconds / 60;
                    int ss = snap.NextScheduledSeconds % 60;
                    _lblSchedCountdown.Text = $"next in {mm:D2}:{ss:D2}";
                }
                else
                {
                    _lblSchedCountdown.Text = "off";
                }

                // Sync Sliders
                _swAutoPilot.IsOn = _engine.Config.AutoEnabled;
                _sliderThreshold.Value = _engine.Config.RamThresholdPercent;
                _lblThresholdVal.Text = $"{_engine.Config.RamThresholdPercent}%";
                _sliderMinFree.Value = _engine.Config.MinFreeRamGb;
                _lblMinFreeVal.Text = $"{_engine.Config.MinFreeRamGb:F1} GB";

                // Last Sweep Bar & Event Log
                if (events.Count > 0)
                {
                    var latest = events[0];
                    var (latestTitle, _) = DescribeEvent(latest);
                    _lastSweepSummaryText.Text = $"{latest.Timestamp} · {latestTitle}";
                }
                RenderEventLog(events);
                RenderProcessLists(procs);
                DrawTelemetryChart(telemetry, snap.EffectiveThresholdPercent);

                if (_currentView == "profiles")
                {
                    RenderProfilesGrid();
                }
            }
            catch
            {
                // Keep UI loop resilient across telemetry ticks
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private static void UpdateGaugeArc(Microsoft.UI.Xaml.Shapes.Path path, double cx, double cy, double r, double fraction)
        {
            double clamped = Math.Clamp(fraction, 0.005, 0.999);
            double angleRad = clamped * 2.0 * Math.PI;
            double startX = cx;
            double startY = cy - r;
            double endX = cx + r * Math.Sin(angleRad);
            double endY = cy - r * Math.Cos(angleRad);

            var fig = new PathFigure { StartPoint = new Point(startX, startY), IsClosed = false };
            fig.Segments.Add(new ArcSegment
            {
                Point = new Point(endX, endY),
                Size = new Size(r, r),
                IsLargeArc = clamped > 0.5,
                SweepDirection = SweepDirection.Clockwise
            });
            var geom = new PathGeometry();
            geom.Figures.Add(fig);
            path.Data = geom;
        }

        private void UpdateThresholdMarkerDot(double cx, double cy, double r, double thresholdPercent)
        {
            double angleRad = (Math.Clamp(thresholdPercent, 1.0, 99.0) / 100.0) * 2.0 * Math.PI;
            double x = cx + r * Math.Sin(angleRad) - 6;
            double y = cy - r * Math.Cos(angleRad) - 6;
            Canvas.SetLeft(_gaugeThresholdDot, x);
            Canvas.SetTop(_gaugeThresholdDot, y);
        }

        private void DrawTelemetryChart(List<TelemetryPoint> pts, int thresholdPercent)
        {
            if (_chartCanvas == null) return;
            double w = _chartCanvas.ActualWidth;
            double h = _chartCanvas.ActualHeight;
            if (w < 40 || h < 30) return;

            _chartCanvas.Children.Clear();

            // Subtle Grid Lines
            for (int pct = 25; pct <= 75; pct += 25)
            {
                double gy = h - (pct / 100.0) * h;
                _chartCanvas.Children.Add(new Line
                {
                    X1 = 0, Y1 = gy, X2 = w, Y2 = gy,
                    Stroke = BrushHex("#94A3B8", 22),
                    StrokeThickness = 1
                });
            }

            // Red Dashed Trigger Line
            double ty = h - (Math.Clamp(thresholdPercent, 10, 98) / 100.0) * h;
            _chartCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = ty, X2 = w, Y2 = ty,
                Stroke = BrushHex("#F43F5E", 150),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            });

            if (pts.Count < 2) return;
            int count = pts.Count;
            double stepX = w / Math.Max(1, 59);

            var ramPoly = new Polyline { Stroke = BrushHex("#38BDF8"), StrokeThickness = 2.4 };
            var cpuPoly = new Polyline { Stroke = BrushHex("#10B981", 130), StrokeThickness = 1.5 };
            var areaPoly = new Polygon { Fill = BrushHex("#38BDF8", 40) };

            areaPoly.Points.Add(new Point(0, h));
            for (int i = 0; i < count; i++)
            {
                double x = i * stepX;
                double ry = h - (Math.Clamp(pts[i].UsedPercent, 0, 100) / 100.0) * h;
                double cy = h - (Math.Clamp(pts[i].CpuPercent, 0, 100) / 100.0) * h;
                ramPoly.Points.Add(new Point(x, ry));
                cpuPoly.Points.Add(new Point(x, cy));
                areaPoly.Points.Add(new Point(x, ry));
            }
            areaPoly.Points.Add(new Point((count - 1) * stepX, h));

            _chartCanvas.Children.Add(areaPoly);
            _chartCanvas.Children.Add(cpuPoly);
            _chartCanvas.Children.Add(ramPoly);
        }

        private void RenderProcessLists(List<ProcessItem> rawItems)
        {
            // 1. Dashboard Top 8 Consumers
            _dashTopProcsPanel.Children.Clear();
            foreach (var item in rawItems.OrderByDescending(p => p.MemoryMb).Take(_engine.LiteMode ? 5 : 8))
            {
                _dashTopProcsPanel.Children.Add(CreateProcessRowElement(item));
            }

            // 2. Full Process Inspector (if visible)
            if (_currentView != "processes") return;

            IEnumerable<ProcessItem> filtered = rawItems;
            if (!string.IsNullOrWhiteSpace(_procSearchQuery))
            {
                filtered = filtered.Where(p =>
                    p.Name.Contains(_procSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                    p.Pid.ToString().Contains(_procSearchQuery));
            }

            filtered = _procFilter switch
            {
                "idle" => filtered.Where(p => (p.SmartScore >= 55 && !p.IsProtected) || p.IsLeaking),
                "governed" => filtered.Where(p => p.IsFrozen || p.IsEco),
                "protected" => filtered.Where(p => p.IsProtected || p.CapMb.HasValue),
                _ => filtered
            };

            filtered = _procSort switch
            {
                "score" => filtered.OrderByDescending(p => p.SmartScore).ThenByDescending(p => p.MemoryMb),
                "cpu" => filtered.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.MemoryMb),
                "leak" => filtered.OrderByDescending(p => p.LeakRateMbPerMin).ThenByDescending(p => p.MemoryMb),
                _ => filtered.OrderByDescending(p => p.MemoryMb)
            };

            _fullProcsPanel.Children.Clear();
            foreach (var item in filtered.Take(_engine.LiteMode ? 25 : 50))
            {
                _fullProcsPanel.Children.Add(CreateProcessRowElement(item));
            }
        }

        private Border CreateProcessRowElement(ProcessItem p)
        {
            var rowBorder = new Border
            {
                Background = BrushHex("#060A12", 165),
                BorderBrush = BrushHex("#94A3B8", 30),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 7, 11, 7)
            };

            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Col 0: Identity
            var identRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
            identRow.Children.Add(new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Background = BrushHex("#38BDF8", 35),
                BorderBrush = BrushHex("#38BDF8", 90),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = p.Name.Length >= 2 ? p.Name.Substring(0, 2).ToUpperInvariant() : "AP",
                    FontSize = 10.5,
                    FontWeight = FontWeights.ExtraBold,
                    Foreground = BrushHex("#38BDF8"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });

            var nameStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock
            {
                Text = p.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushHex("#F8FAFC"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            string meta = _groupedProcesses && p.ProcessCount > 1
                ? $"{p.ProcessCount} procs · {p.CpuPercent:F1}% CPU"
                : $"PID {p.Pid} · {p.CpuPercent:F1}% CPU";
            if (p.CapMb.HasValue) meta += $" · Cap {p.CapMb.Value}M";
            if (p.LeakRateMbPerMin >= 15) meta += $" · +{p.LeakRateMbPerMin:F0}M/m";

            nameStack.Children.Add(new TextBlock
            {
                Text = meta,
                FontSize = 10.5,
                Foreground = BrushHex("#94A3B8"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            identRow.Children.Add(nameStack);
            Grid.SetColumn(identRow, 0);
            g.Children.Add(identRow);

            // Col 1: Status Badge
            var badgeBorder = new Border
            {
                Background = BrushHex(p.StatusColorHex, 35),
                BorderBrush = BrushHex(p.StatusColorHex, 110),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = p.StatusBadge,
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushHex(p.StatusColorHex)
                }
            };
            Grid.SetColumn(badgeBorder, 1);
            g.Children.Add(badgeBorder);

            // Col 2: Memory MB + Score
            var memStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            memStack.Children.Add(new TextBlock
            {
                Text = FormatMb(p.MemoryMb),
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#F8FAFC")
            });
            memStack.Children.Add(new TextBlock
            {
                Text = $"Trim Score: {p.SmartScore}",
                FontSize = 10,
                Foreground = BrushHex("#64748B")
            });
            Grid.SetColumn(memStack, 2);
            g.Children.Add(memStack);

            // Col 3: 5 Action Governor Buttons (Trim | Eco | Freeze | Cap | Shield)
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

            var btnTrim = CreateMiniActionButton("Trim", false, "#38BDF8", () =>
            {
                string appName = p.Name;
                TriggerReactorShockwaveAnimation();
                RunEngineActionAsync(() => _engine.OptimizeSingleApp(appName, "Manual App Trim"), ev =>
                    ShowToast(ev.Skipped ? $"Could not trim {appName}" : $"Trimmed {appName}",
                        ev.Skipped ? ev.Note : $"{FormatMb(ev.FreedMb)} paged out of {ev.ProcessesTrimmed} process(es)."), "trim:" + appName);
            });
            var btnEco = CreateMiniActionButton("Eco", p.IsEco, "#2DD4BF", () =>
            {
                string appName = p.Name;
                RunEngineActionAsync(() => _engine.ToggleEcoApp(appName), res =>
                    ShowToast(res.Ok ? (res.EcoActive ? $"EcoQoS Active: {appName}" : $"EcoQoS Off: {appName}") : "Not Changed", res.Message), "eco:" + appName);
            });
            var btnFreeze = CreateMiniActionButton(p.IsFrozen ? "Thaw" : "Freeze", p.IsFrozen, "#60A5FA", () =>
            {
                string appName = p.Name;
                RunEngineActionAsync(() => _engine.ToggleFreezeApp(appName), res =>
                    ShowToast(res.Ok ? (res.Frozen ? $"Frozen {appName}" : $"Thawed {appName}") : (res.Frozen ? "Still frozen" : "Not frozen"), res.Message), "freeze:" + appName);
            });
            var btnCap = CreateMiniActionButton(p.CapMb.HasValue ? $"{p.CapMb.Value}M" : "Cap", p.CapMb.HasValue, "#38BDF8", () =>
            {
                ShowAppMemoryCapDialogAsync(p);
            });
            var btnShield = CreateMiniActionButton(p.IsProtected ? "Shielded" : "Shield", p.IsProtected, "#10B981", () =>
            {
                string appName = p.Name;
                RunEngineActionAsync(() => _engine.ToggleProtectApp(appName), nowProt =>
                    ShowToast(nowProt ? $"Shielded {appName}" : $"Unshielded {appName}",
                        nowProt ? "Never trimmed by any sweep." : "Removed from the shield list."), "shield:" + appName);
            });

            actions.Children.Add(btnTrim);
            actions.Children.Add(btnEco);
            actions.Children.Add(btnFreeze);
            actions.Children.Add(btnCap);
            actions.Children.Add(btnShield);

            Grid.SetColumn(actions, 3);
            g.Children.Add(actions);

            rowBorder.Child = g;
            return rowBorder;
        }

        private static Button CreateMiniActionButton(string text, bool active, string accentHex, Action onClick)
        {
            var btn = new Button
            {
                Content = text,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(6),
                Background = active ? BrushHex(accentHex, 48) : BrushHex("#151F35", 190),
                BorderBrush = active ? BrushHex(accentHex) : BrushHex("#94A3B8", 40),
                Foreground = active ? BrushHex(accentHex) : BrushHex("#F8FAFC"),
                BorderThickness = new Thickness(1)
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private async void ShowAppMemoryCapDialogAsync(ProcessItem proc)
        {
            var dialog = new ContentDialog
            {
                Title = $"Memory Cap — {proc.Name}",
                PrimaryButtonText = "Apply Cap",
                SecondaryButtonText = "Remove Cap",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = s_lightTheme ? ElementTheme.Light : ElementTheme.Dark,
                XamlRoot = _rootGrid.XamlRoot
            };

            var sp = new StackPanel { Spacing = 10 };
            sp.Children.Add(new TextBlock
            {
                Text = $"Now {FormatMb(proc.MemoryMb)}. Hard limit per process — set it too low and the app slows down.",
                FontSize = 12,
                Foreground = BrushHex("#94A3B8"),
                TextWrapping = TextWrapping.Wrap
            });

            var inputBox = new TextBox
            {
                Header = "Limit per process (MB)",
                Text = (proc.CapMb ?? Math.Max(256, (int)(proc.MemoryMb * 0.75))).ToString()
            };

            var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (int preset in new[] { 256, 512, 1024, 2048, 4096 })
            {
                int val = preset;
                var pBtn = new Button
                {
                    Content = val >= 1024 ? $"{val / 1024} GB" : $"{val} MB",
                    FontSize = 11,
                    Padding = new Thickness(8, 4, 8, 4)
                };
                pBtn.Click += (s, e) => inputBox.Text = val.ToString();
                presetRow.Children.Add(pBtn);
            }

            sp.Children.Add(presetRow);
            sp.Children.Add(inputBox);
            dialog.Content = sp;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (int.TryParse(inputBox.Text.Trim(), out int capMb) && capMb >= 64)
                {
                    await System.Threading.Tasks.Task.Run(() => _engine.SetAppMemoryCap(proc.Name, capMb));
                    RefreshAllUi();
                    ShowToast($"Capped {proc.Name} at {capMb} MB",
                        "Hard working-set limit applied. Windows pages the app out above this, so a cap that is too low makes it slow.");
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await System.Threading.Tasks.Task.Run(() => _engine.SetAppMemoryCap(proc.Name, null));
                RefreshAllUi();
                ShowToast($"Removed Cap on {proc.Name}", "Hard working-set limit released.");
            }
        }

        private void RenderEventLog(List<OptimizationEvent> events)
        {
            _eventCountText.Text = $"{events.Count} sweeps";
            _eventLogPanel.Children.Clear();

            if (events.Count == 0)
            {
                _eventLogPanel.Children.Add(new TextBlock
                {
                    Text = "No sweeps yet.",
                    FontSize = 11.5,
                    Foreground = BrushHex("#64748B"),
                    Margin = new Thickness(4, 12, 4, 12),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var ev in events.Take(15))
            {
                var b = new Border
                {
                    Background = BrushHex("#060A12", 165),
                    BorderBrush = BrushHex("#10B981", 80),
                    BorderThickness = new Thickness(3, 1, 1, 1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(11, 8, 11, 8)
                };
                var sp = new StackPanel { Spacing = 2 };
                var top = new Grid();
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                top.Children.Add(new TextBlock
                {
                    Text = ev.Skipped
                        ? $"Skipped ({ev.Mode})"
                        : (ev.Mode == "STANDBY" ? $"+{FormatMb(ev.FreeListGainMb)} freed ({ev.Mode})" : $"{FormatMb(ev.FreedMb)} trimmed ({ev.Mode})"),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushHex("#34D399")
                });
                var timeTxt = new TextBlock { Text = $"{ev.Timestamp} · {ev.DurationMs}ms", FontSize = 10.5, Foreground = BrushHex("#64748B") };
                Grid.SetColumn(timeTxt, 1);
                top.Children.Add(timeTxt);

                sp.Children.Add(top);
                sp.Children.Add(new TextBlock { Text = ev.Reason, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#F8FAFC") });
                sp.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(ev.Note)
                        ? $"RAM {ev.BeforePercent:F1}% → {ev.AfterPercent:F1}% · {ev.ProcessesTrimmed} processes trimmed"
                        : $"RAM {ev.BeforePercent:F1}% → {ev.AfterPercent:F1}% · {ev.ProcessesTrimmed} processes trimmed · {ev.Note}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 10.5,
                    Foreground = BrushHex("#94A3B8")
                });
                b.Child = sp;
                _eventLogPanel.Children.Add(b);
            }
        }

        private void RenderProfilesGrid()
        {
            _profilesGrid.Children.Clear();
            var profiles = _engine.Config.Profiles;
            for (int i = 0; i < profiles.Count && i < 4; i++)
            {
                var prof = profiles[i];
                bool isForced = _engine.Config.ForcedProfileId == prof.Id;

                var card = new Border
                {
                    Background = isForced ? BrushHex("#10B981", 28) : BrushHex("#060A12", 175),
                    BorderBrush = isForced ? BrushHex("#10B981") : BrushHex("#94A3B8", 38),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14)
                };

                var sp = new StackPanel { Spacing = 8 };
                ToolTipService.SetToolTip(card, prof.Description);
                sp.Children.Add(new TextBlock { Text = prof.Name, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = BrushHex("#F8FAFC") });
                sp.Children.Add(new TextBlock
                {
                    Text = $"{prof.RamThresholdPercent}% · {prof.AutoMode}{(prof.AutoShieldMatched ? " · shields app" : "")}",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushHex("#38BDF8")
                });

                var actBtn = new Button
                {
                    Content = isForced ? "✓ Active" : "Activate",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    CornerRadius = new CornerRadius(8),
                    Background = isForced ? BrushHex("#10B981", 45) : BrushHex("#151F35", 190),
                    BorderBrush = isForced ? BrushHex("#10B981") : BrushHex("#38BDF8", 90),
                    BorderThickness = new Thickness(1)
                };
                string pid = prof.Id;
                string pname = prof.Name;
                actBtn.Click += (s, e) =>
                {
                    _engine.Config.ForcedProfileId = isForced ? "" : pid;
                    _engine.SaveConfig();
                    RefreshAllUi();
                    ShowToast(isForced ? "Returned to Auto-Detect" : $"Activated {pname}",
                        isForced ? "Automatic workload detection restored." : $"Locked Auto-Pilot to {pname} profile.");
                };
                sp.Children.Add(actBtn);
                card.Child = sp;

                Grid.SetRow(card, i / 2);
                Grid.SetColumn(card, i % 2);
                _profilesGrid.Children.Add(card);
            }
        }

        #endregion
    }
}
