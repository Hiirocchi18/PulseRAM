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
    /// <summary>The compact always-on-top widget.</summary>
    public sealed partial class MainWindow
    {
        // Compact widget (its own small layout, not a shrunk dashboard)
        private Grid _fullLayout = null!;
        private Border _miniHudPanel = null!;
        private ProgressRing _miniRing = null!;
        private TextBlock _miniPercentText = null!;
        private TextBlock _miniUsedText = null!;
        private TextBlock _miniStatusText = null!;
        private Button _miniCleanBtn = null!;
        private TextBlock _miniCleanLabel = null!;
        private DateTime _miniStatusUntil = DateTime.MinValue;

        private DateTime _miniLastPress = DateTime.MinValue;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        private const uint WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;

        private void ToggleMiniHudMode()
        {
            if (!_isMiniHud) SaveWindowPlacement(); // remember the full-size window before shrinking
            _isMiniHud = !_isMiniHud;
            ApplyMiniHudLayout();

            try
            {
                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    if (_isMiniHud && presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
                    presenter.IsAlwaysOnTop = _isMiniHud;
                    presenter.IsResizable = !_isMiniHud;
                    presenter.IsMaximizable = !_isMiniHud;
                    // A widget has no title bar: it is dragged by its body and has its own two buttons.
                    presenter.SetBorderAndTitleBar(true, !_isMiniHud);
                }
                if (_isMiniHud)
                {
                    PlaceMiniHud();
                    ScheduleMiniSeeThrough(); // fades after a moment unless the mouse is over it
                }
                else
                {
                    // Back to the user's own size (or a size that fits the screen), never a fixed 1440×930.
                    _miniFadeTimer?.Stop();
                    ApplyMiniWindowAlpha(255, removeLayered: true);
                    RestoreWindowPlacement();
                    if (_engine.Config.WindowMaximized && _appWindow.Presenter is OverlappedPresenter op) op.Maximize();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Compact view switch failed", ex);
            }
            RefreshAllUi(force: true);
        }

        /// <summary>Element states for compact view on/off (also re-applied after a theme rebuild).</summary>
        private void ApplyMiniHudLayout()
        {
            _fullLayout.Visibility = _isMiniHud ? Visibility.Collapsed : Visibility.Visible;
            _miniHudPanel.Visibility = _isMiniHud ? Visibility.Visible : Visibility.Collapsed;
        }

        private const int MiniHudWidthDip = 304;
        private const int MiniHudHeightDip = 104;

        /// <summary>Sizes the widget and puts it where it was last time, or in the top-right corner of this screen.</summary>
        private void PlaceMiniHud()
        {
            double scale = Math.Max(1.0, GetDpiForWindow(_hwnd) / 96.0);
            var cfg = _engine.Config;
            DisplayArea? area = null;
            if (cfg.MiniHudX is int mx && cfg.MiniHudY is int my)
                area = DisplayArea.GetFromPoint(new PointInt32(mx + 40, my + 20), DisplayAreaFallback.None);
            if (area != null)
            {
                _appWindow.Move(new PointInt32(cfg.MiniHudX!.Value, cfg.MiniHudY!.Value));
                scale = Math.Max(1.0, GetDpiForWindow(_hwnd) / 96.0); // the target screen may use another scale
            }
            else
            {
                var wa = (DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary)?.WorkArea) ?? new RectInt32(0, 0, 1280, 720);
                int margin = (int)(16 * scale);
                _appWindow.Move(new PointInt32(wa.X + wa.Width - (int)(MiniHudWidthDip * scale) - margin, wa.Y + margin));
            }
            _appWindow.ResizeClient(new SizeInt32((int)(MiniHudWidthDip * scale), (int)(MiniHudHeightDip * scale)));
        }

        private static Button MiniIconButton(string glyph, string tip, Action onClick)
        {
            var b = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 11 },
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = BrushHex("#94A3B8")
            };
            b.Click += (s, e) => onClick();
            ToolTipService.SetToolTip(b, tip);
            return b;
        }

        /// <summary>
        /// The compact widget: RAM ring, used/total, one status line and one Clean button.
        /// Drag it anywhere by its body; it stays on top.
        /// </summary>
        private Border BuildMiniHudPanel()
        {
            var panel = new Border
            {
                Background = BrushHex("#0D1424"),
                Padding = new Thickness(14, 12, 8, 12),
                Visibility = Visibility.Collapsed
            };

            var grid = new Grid { ColumnSpacing = 14 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // RAM ring with the percentage inside
            var ringHost = new Grid { Width = 72, Height = 72, VerticalAlignment = VerticalAlignment.Center };
            _miniRing = new ProgressRing
            {
                IsIndeterminate = false,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 72,
                Height = 72,
                Foreground = BrushHex("#10B981"),
                Background = BrushHex("#94A3B8", 40),
                IsHitTestVisible = false
            };
            _miniPercentText = new TextBlock
            {
                Text = "–",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#F8FAFC"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            ringHost.Children.Add(_miniRing);
            ringHost.Children.Add(_miniPercentText);
            Grid.SetColumn(ringHost, 0);
            grid.Children.Add(ringHost);

            // Used / total, status, Clean
            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            _miniUsedText = new TextBlock
            {
                Text = "– / – GB",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushHex("#F8FAFC"),
                IsHitTestVisible = false
            };
            _miniStatusText = new TextBlock
            {
                Text = "",
                FontSize = 11.5,
                Foreground = BrushHex("#94A3B8"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            };
            _miniCleanLabel = new TextBlock { Text = "Clean", FontSize = 12, FontWeight = FontWeights.SemiBold };
            var cleanContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            cleanContent.Children.Add(new FontIcon { Glyph = "\uE945", FontSize = 12 });
            cleanContent.Children.Add(_miniCleanLabel);
            _miniCleanBtn = new Button
            {
                Content = cleanContent,
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 6, 0, 0),
                CornerRadius = new CornerRadius(8),
                Background = BrushHex("#38BDF8", 50),
                BorderBrush = BrushHex("#38BDF8", 140),
                BorderThickness = new Thickness(1),
                Foreground = BrushHex("#F8FAFC")
            };
            _miniCleanBtn.Click += (s, e) => RunOptimizationAsync(_selectedMode, "Compact view");
            info.Children.Add(_miniUsedText);
            info.Children.Add(_miniStatusText);
            info.Children.Add(_miniCleanBtn);
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            // Expand / hide
            var side = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
            side.Children.Add(MiniIconButton("\uE740", "Full view", ToggleMiniHudMode));
            side.Children.Add(MiniIconButton("\uE921", "Hide to tray", () => ShowWindow(_hwnd, 0)));
            Grid.SetColumn(side, 2);
            grid.Children.Add(side);

            panel.Child = grid;

            // No title bar: drag the widget by its body (buttons handle their own presses).
            panel.PointerPressed += (s, e) =>
            {
                try
                {
                    if (!e.GetCurrentPoint(panel).Properties.IsLeftButtonPressed) return;
                    // The window move loop swallows the second click, so detect a double-click here.
                    var now = DateTime.Now;
                    if ((now - _miniLastPress).TotalMilliseconds <= GetDoubleClickTime())
                    {
                        _miniLastPress = DateTime.MinValue;
                        e.Handled = true;
                        DispatcherQueue.TryEnqueue(ToggleMiniHudMode);
                        return;
                    }
                    _miniLastPress = now;
                    ReleaseCapture();
                    SendMessage(_hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
                }
                catch { }
            };
            ToolTipService.SetToolTip(ringHost, "Drag to move · double-click for full view · right-click for options");

            panel.ContextFlyout = BuildMiniHudMenu();
            panel.PointerEntered += (s, e) => SetMiniSeeThrough(false);
            panel.PointerExited += (s, e) => ScheduleMiniSeeThrough();
            return panel;
        }

        // ---- Right-click menu ----

        private bool _miniMenuOpen;

        private MenuFlyout BuildMiniHudMenu()
        {
            var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
            var modes = new (string Key, string Label)[] { ("smart", "Smart"), ("deep", "Deep"), ("standby", "Standby"), ("max", "Max") };
            var modeItems = new List<RadioMenuFlyoutItem>();
            foreach (var (key, label) in modes)
            {
                var item = new RadioMenuFlyoutItem { Text = $"Clean mode: {label}", GroupName = "pulseram-mode", Tag = key };
                item.Click += (s, e) => { SelectCleaningMode(key); RefreshAllUi(force: true); };
                modeItems.Add(item);
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());

            var focus = new ToggleMenuFlyoutItem { Text = "Focus Boost" };
            focus.Click += (s, e) => ToggleFocusBoost();
            var auto = new ToggleMenuFlyoutItem { Text = "Auto-Pilot" };
            auto.Click += (s, e) => _swAutoPilot.IsOn = auto.IsChecked; // the switch's handler saves and refreshes
            var seeThrough = new ToggleMenuFlyoutItem { Text = "See-through when idle" };
            seeThrough.Click += (s, e) =>
            {
                _engine.Config.MiniHudSeeThrough = seeThrough.IsChecked;
                _engine.SaveConfig();
                if (!seeThrough.IsChecked) ApplyMiniWindowAlpha(255, removeLayered: true);
            };
            menu.Items.Add(focus);
            menu.Items.Add(auto);
            menu.Items.Add(seeThrough);
            menu.Items.Add(new MenuFlyoutSeparator());

            var full = new MenuFlyoutItem { Text = "Full view", Icon = new FontIcon { Glyph = "\uE740" } };
            full.Click += (s, e) => ToggleMiniHudMode();
            var hide = new MenuFlyoutItem { Text = "Hide to tray", Icon = new FontIcon { Glyph = "\uE921" } };
            hide.Click += (s, e) => ShowWindow(_hwnd, 0);
            menu.Items.Add(full);
            menu.Items.Add(hide);

            menu.Opening += (s, e) =>
            {
                foreach (var m in modeItems) m.IsChecked = (string)m.Tag == _selectedMode;
                focus.IsChecked = _engine.Config.FocusBoostActive;
                auto.IsChecked = _engine.Config.AutoEnabled;
                seeThrough.IsChecked = _engine.Config.MiniHudSeeThrough;
                _miniMenuOpen = true;
                SetMiniSeeThrough(false);
            };
            menu.Closed += (s, e) =>
            {
                _miniMenuOpen = false;
                ScheduleMiniSeeThrough();
            };
            return menu;
        }

        // ---- See-through when the mouse isn't over the widget ----

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtrEx(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_LAYERED = 0x00080000;
        private const uint LWA_ALPHA = 0x2;
        private const byte MiniSeeThroughAlpha = 150;

        private DispatcherQueueTimer? _miniFadeTimer;

        private void ScheduleMiniSeeThrough()
        {
            if (!_isMiniHud || !_engine.Config.MiniHudSeeThrough || _miniMenuOpen) return;
            if (_miniFadeTimer == null)
            {
                _miniFadeTimer = DispatcherQueue.CreateTimer();
                _miniFadeTimer.Interval = TimeSpan.FromMilliseconds(1200);
                _miniFadeTimer.IsRepeating = false;
                _miniFadeTimer.Tick += (s, e) =>
                {
                    if (_isMiniHud && _engine.Config.MiniHudSeeThrough && !_miniMenuOpen) SetMiniSeeThrough(true);
                };
            }
            _miniFadeTimer.Stop();
            _miniFadeTimer.Start();
        }

        private void SetMiniSeeThrough(bool seeThrough)
        {
            _miniFadeTimer?.Stop();
            if (seeThrough) ApplyMiniWindowAlpha(MiniSeeThroughAlpha, removeLayered: false);
            else if (_isMiniHud && _engine.Config.MiniHudSeeThrough) ApplyMiniWindowAlpha(255, removeLayered: false);
        }

        /// <summary>Whole-window opacity via a layered window; removing the style returns to normal rendering.</summary>
        private void ApplyMiniWindowAlpha(byte alpha, bool removeLayered)
        {
            try
            {
                long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
                if (removeLayered)
                {
                    if ((ex & WS_EX_LAYERED) != 0) SetWindowLongPtrEx(_hwnd, GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_LAYERED));
                    return;
                }
                if ((ex & WS_EX_LAYERED) == 0) SetWindowLongPtrEx(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
                SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);
            }
            catch (Exception ex)
            {
                Log.Warn("See-through mode failed", ex);
            }
        }

        private void UpdateMiniHud(MemorySnapshot snap)
        {
            string stateHex = snap.PressureLevel == "CRITICAL" ? "#F43F5E"
                : (snap.PressureLevel == "ELEVATED" ? "#F59E0B" : "#10B981");
            _miniRing.Value = Math.Clamp(snap.Percent, 0, 100);
            _miniRing.Foreground = BrushHex(stateHex);
            _miniPercentText.Text = $"{snap.Percent:F0}%";
            _miniUsedText.Text = $"{snap.UsedGb:F1} / {snap.TotalGb:F1} GB";

            if (_miniCleanBtn.IsEnabled)
            {
                _miniCleanLabel.Text = _selectedMode switch
                {
                    "deep" => "Clean · Deep",
                    "standby" => "Clean · Standby",
                    "max" => "Clean · Max",
                    _ => "Clean"
                };
            }

            if (DateTime.Now < _miniStatusUntil) return; // a recent result is still showing

            if (snap.CommitLevel != "ok")
            {
                _miniStatusText.Text = $"Memory limit {snap.CommitPercent:F0}% used";
                _miniStatusText.Foreground = BrushHex(snap.CommitLevel == "critical" ? "#F43F5E" : "#F59E0B");
            }
            else
            {
                string auto = _engine.Config.AutoEnabled ? $"Auto {snap.EffectiveThresholdPercent}%" : "Auto off";
                _miniStatusText.Text = $"{snap.AvailableGb:F1} GB free · {auto}";
                _miniStatusText.Foreground = BrushHex("#94A3B8");
            }
        }
    }
}
