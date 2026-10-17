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
    public sealed class MainWindow : Window
    {
        #region Win32 Hotkey, Window Subclass, Icon, & System Tray P/Invoke

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW", CharSet = CharSet.Unicode)]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
        }

        private const int GWLP_WNDPROC = -4;
        private const uint WM_SETICON = 0x0080;
        private const uint WM_HOTKEY = 0x0312;
        private const uint WM_USER_TRAY = 0x0400 + 77;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONUP = 0x0205;

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;

        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_NONOTIFY = 0x0080;
        private const uint MF_STRING = 0x0000;
        private const uint MF_SEPARATOR = 0x0800;

        #endregion

        private readonly string[] _launchArgs;
        private readonly NativeMemoryEngine _engine;
        private readonly IntPtr _hwnd;
        private readonly AppWindow _appWindow;
        private WndProcDelegate? _wndProcDelegate;
        private IntPtr _oldWndProc = IntPtr.Zero;
        private IntPtr _customHIcon = IntPtr.Zero;
        private NOTIFYICONDATA _trayData;
        private bool _trayInitialized = false;

        private readonly DispatcherQueueTimer _uiTimer;
        private readonly DispatcherQueueTimer _gaugeAnimTimer;
        private bool _isUpdatingUi = false;
        private string _currentView = "overview";
        private string _selectedMode = "smart";
        private bool _groupedProcesses = true;
        private string _procFilter = "all";
        private string _procSort = "memory";
        private string _procSearchQuery = "";
        private bool _isMiniHud = false;

        // 60 FPS Smooth Gauge Interpolation State
        private double _targetPercent = 50.0;
        private double _displayedPercent = 0.0;
        private double _targetUsedRatio = 0.50;
        private double _displayedUsedRatio = 0.01;
        private double _targetCombinedRatio = 0.65;
        private double _displayedCombinedRatio = 0.01;
        private double _targetThresholdPct = 80.0;
        private double _displayedThresholdPct = 80.0;

        // XAML Storyboards
        private Storyboard? _reactorBreathingStoryboard;
        private Storyboard? _reactorOrbitalStoryboard;
        private Storyboard? _logoPulseStoryboard;

        // Root Layout Containers
        private Grid _rootGrid = null!;
        private Grid _viewOverview = null!;
        private Grid _viewProcesses = null!;
        private Grid _viewProfiles = null!;
        private Grid _rightColumnOverview = null!;
        private Grid _bottomSplitOverview = null!;

        // Top Command Bar Controls
        private Ellipse _logoGlowRing = null!;
        private Button _tabBtnOverview = null!;
        private Button _tabBtnProcesses = null!;
        private Button _tabBtnProfiles = null!;
        private TextBlock _tabProcCountText = null!;
        private Button _focusBoostBtn = null!;
        private TextBlock _apStatusPillText = null!;
        private Ellipse _apStatusDot = null!;
        private TextBlock _sessionFreedTopText = null!;
        private Button _miniHudBtn = null!;
        private Button _adminBtn = null!;

        // Memory Reactor Controls & Animated Layers
        private TextBlock _clockText = null!;
        private Canvas _gaugeCanvas = null!;
        private Ellipse _reactorPulseHalo = null!;
        private Ellipse _reactorOrbitalRing = null!;
        private Ellipse _reactorShockwaveRing = null!;
        private Microsoft.UI.Xaml.Shapes.Path _gaugeCachePath = null!;
        private Microsoft.UI.Xaml.Shapes.Path _gaugeUsedPath = null!;
        private Ellipse _gaugeThresholdDot = null!;
        private TextBlock _gaugePercentText = null!;
        private TextBlock _gaugeSubText = null!;
        private Border _pressureBadgeBorder = null!;
        private TextBlock _pressureBadgeText = null!;

        // Physical Memory Map Bar
        private TextBlock _availSummaryText = null!;
        private ColumnDefinition _colSegUsed = null!;
        private ColumnDefinition _colSegCache = null!;
        private ColumnDefinition _colSegFree = null!;
        private TextBlock _legUsedText = null!;
        private TextBlock _legCacheText = null!;
        private TextBlock _legAvailText = null!;

        // Mode Selector & CTA
        private readonly Dictionary<string, Border> _modeCards = new Dictionary<string, Border>();
        private Button _optimizeNowBtn = null!;
        private TextBlock _optimizeBtnLabel = null!;
        private TextBlock _optimizeBtnSub = null!;
        private TextBlock _lastSweepSummaryText = null!;

        // Telemetry Chart & 6 Kernel Vitals
        private Canvas _chartCanvas = null!;
        private TextBlock _chartMetaText = null!;
        private TextBlock _vitReclaimedVal = null!;
        private TextBlock _vitReclaimedSub = null!;
        private TextBlock _vitCommitVal = null!;
        private TextBlock _vitCommitSub = null!;
        private TextBlock _vitPagefileVal = null!;
        private TextBlock _vitPagefileSub = null!;
        private TextBlock _vitKernelVal = null!;
        private TextBlock _vitKernelSub = null!;
        private TextBlock _vitCpuVal = null!;
        private TextBlock _vitCpuSub = null!;
        private TextBlock _vitGovVal = null!;
        private TextBlock _vitGovSub = null!;

        // Auto-Pilot & Active Guards Controls
        private ToggleSwitch _swAutoPilot = null!;
        private Slider _sliderThreshold = null!;
        private TextBlock _lblThresholdVal = null!;
        private Slider _sliderMinFree = null!;
        private TextBlock _lblMinFreeVal = null!;
        private ComboBox _cmbAutoMode = null!;
        private ComboBox _cmbCooldown = null!;
        private ToggleSwitch _swProtectFg = null!;
        private CheckBox _chkSoundChime = null!;
        private ToggleSwitch _swLeakGuard = null!;
        private ToggleSwitch _swScheduled = null!;
        private ComboBox _cmbSchedInterval = null!;
        private TextBlock _lblSchedCountdown = null!;
        private ToggleSwitch _swStartup = null!;

        // Dashboard Top Consumers & Event Log
        private StackPanel _dashTopProcsPanel = null!;
        private StackPanel _eventLogPanel = null!;
        private TextBlock _eventCountText = null!;

        // Process Inspector View Controls
        private TextBox _searchBox = null!;
        private ComboBox _cmbSortProcs = null!;
        private Button _btnGroupApps = null!;
        private Button _btnGroupPids = null!;
        private readonly Dictionary<string, Button> _filterButtons = new Dictionary<string, Button>();
        private StackPanel _fullProcsPanel = null!;

        // Profiles & Hotkeys View Controls
        private ToggleSwitch _swProfileAuto = null!;
        private Grid _profilesGrid = null!;
        private ToggleSwitch _swHotkeys = null!;

        // Toast Banner
        private Border _toastBanner = null!;
        private TextBlock _toastTitleText = null!;
        private TextBlock _toastSubText = null!;
        private DispatcherQueueTimer _toastTimer = null!;

        public MainWindow(string[] args)
        {
            _launchArgs = args ?? Array.Empty<string>();
            Title = "PulseRAM — Intelligent Kernel Memory Optimizer (WinUI 3)";
            _engine = new NativeMemoryEngine();
            _engine.OnOptimizationCompleted += OnEngineOptimizationCompleted;

            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            ConfigureWindowFrameAndIcon();
            BuildWinUI3Interface();

            Closed += OnMainWindowClosed;

            // Initial UI Render
            RefreshAllUi();

            // 60 FPS Smooth Gauge & Counter Interpolation Timer
            _gaugeAnimTimer = DispatcherQueue.CreateTimer();
            _gaugeAnimTimer.Interval = TimeSpan.FromMilliseconds(16);
            _gaugeAnimTimer.Tick += OnGaugeAnimationFrame;
            _gaugeAnimTimer.Start();

            // 1.5s Live Kernel Telemetry Poll Timer
            _uiTimer = DispatcherQueue.CreateTimer();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(1500);
            _uiTimer.Tick += (s, e) => RefreshAllUi();
            _uiTimer.Start();
        }

        public void InitializeNativeHooksAfterActivate()
        {
            try
            {
                // Subclass HWND AFTER Activate() so WinUI 3's internal WindowProc is already chained
                _wndProcDelegate = CustomWndProc;
                _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

                InitializeSystemTrayIcon();
                RegisterGlobalHotkeys();
                StartAmbientXamlAnimations();

                if (_launchArgs.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)))
                {
                    ShowWindow(_hwnd, 0);
                }
                else
                {
                    ShowWindow(_hwnd, 9); // SW_RESTORE
                    SetForegroundWindow(_hwnd);
                }
            }
            catch { }
        }

        private void ConfigureWindowFrameAndIcon()
        {
            try
            {
                _appWindow.Title = "PulseRAM — Intelligent Kernel Memory Optimizer (WinUI 3)";
            }
            catch { }

            try
            {
                if (MicaController.IsSupported())
                {
                    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                }
                else if (DesktopAcrylicController.IsSupported())
                {
                    SystemBackdrop = new DesktopAcrylicBackdrop();
                }
            }
            catch { }

            try
            {
                string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PulseRAM.ico");
                if (File.Exists(icoPath))
                {
                    _appWindow.SetIcon(icoPath);
                    _customHIcon = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                    if (_customHIcon != IntPtr.Zero)
                    {
                        SendMessage(_hwnd, WM_SETICON, new IntPtr(0), _customHIcon);
                        SendMessage(_hwnd, WM_SETICON, new IntPtr(1), _customHIcon);
                    }
                }
            }
            catch { }

            try
            {
                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    var tb = _appWindow.TitleBar;
                    tb.BackgroundColor = Color.FromArgb(255, 6, 10, 18);
                    tb.InactiveBackgroundColor = Color.FromArgb(255, 6, 10, 18);
                    tb.ForegroundColor = Color.FromArgb(255, 248, 250, 252);
                    tb.ButtonBackgroundColor = Color.FromArgb(255, 6, 10, 18);
                    tb.ButtonHoverBackgroundColor = Color.FromArgb(255, 30, 41, 59);
                    tb.ButtonForegroundColor = Color.FromArgb(255, 248, 250, 252);
                }

                _appWindow.Resize(new SizeInt32(1440, 930));
                var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    int x = Math.Max(0, (displayArea.WorkArea.Width - 1440) / 2);
                    int y = Math.Max(0, (displayArea.WorkArea.Height - 930) / 2);
                    _appWindow.Move(new PointInt32(x, y));
                }
            }
            catch { }
        }

        #region Custom WinUI 3 XAML Animations

        private static TransitionCollection CreateFluentTransitions()
        {
            return new TransitionCollection
            {
                new EntranceThemeTransition { IsStaggeringEnabled = true },
                new RepositionThemeTransition()
            };
        }

        private void StartAmbientXamlAnimations()
        {
            try
            {
                // 1. Reactor Halo Breathing Animation (Scale + Opacity)
                _reactorBreathingStoryboard = new Storyboard();
                var haloOpacity = new DoubleAnimation
                {
                    From = 0.18,
                    To = 0.58,
                    Duration = new Duration(TimeSpan.FromMilliseconds(2200)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EnableDependentAnimation = true,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Storyboard.SetTarget(haloOpacity, _reactorPulseHalo);
                Storyboard.SetTargetProperty(haloOpacity, "Opacity");
                _reactorBreathingStoryboard.Children.Add(haloOpacity);
                _reactorBreathingStoryboard.Begin();

                // 2. Reactor Dashed Orbital Ring Continuous 360 Rotation
                _reactorOrbitalStoryboard = new Storyboard();
                var rotAnim = new DoubleAnimation
                {
                    From = 0.0,
                    To = 360.0,
                    Duration = new Duration(TimeSpan.FromSeconds(18)),
                    RepeatBehavior = RepeatBehavior.Forever,
                    EnableDependentAnimation = true
                };
                Storyboard.SetTarget(rotAnim, _reactorOrbitalRing);
                Storyboard.SetTargetProperty(rotAnim, "(UIElement.RenderTransform).(CompositeTransform.Rotation)");
                _reactorOrbitalStoryboard.Children.Add(rotAnim);
                _reactorOrbitalStoryboard.Begin();

                // 3. Brand Emblem Pulse Animation
                _logoPulseStoryboard = new Storyboard();
                var logoPulse = new DoubleAnimation
                {
                    From = 0.25,
                    To = 0.85,
                    Duration = new Duration(TimeSpan.FromMilliseconds(1600)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EnableDependentAnimation = true,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Storyboard.SetTarget(logoPulse, _logoGlowRing);
                Storyboard.SetTargetProperty(logoPulse, "Opacity");
                _logoPulseStoryboard.Children.Add(logoPulse);
                _logoPulseStoryboard.Begin();
            }
            catch { }
        }

        private void TriggerReactorShockwaveAnimation()
        {
            try
            {
                _reactorShockwaveRing.Visibility = Visibility.Visible;
                var sb = new Storyboard();

                var scaleX = new DoubleAnimation
                {
                    From = 0.72,
                    To = 1.18,
                    Duration = new Duration(TimeSpan.FromMilliseconds(680)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(scaleX, _reactorShockwaveRing);
                Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)");

                var scaleY = new DoubleAnimation
                {
                    From = 0.72,
                    To = 1.18,
                    Duration = new Duration(TimeSpan.FromMilliseconds(680)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(scaleY, _reactorShockwaveRing);
                Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");

                var fade = new DoubleAnimation
                {
                    From = 0.95,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(680)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fade, _reactorShockwaveRing);
                Storyboard.SetTargetProperty(fade, "Opacity");

                sb.Children.Add(scaleX);
                sb.Children.Add(scaleY);
                sb.Children.Add(fade);
                sb.Begin();
            }
            catch { }
        }

        private static void AnimateViewEntrance(UIElement view)
        {
            try
            {
                view.RenderTransform = new CompositeTransform { TranslateY = 14 };
                var sb = new Storyboard();

                var fade = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fade, view);
                Storyboard.SetTargetProperty(fade, "Opacity");

                var slide = new DoubleAnimation
                {
                    From = 14.0,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(slide, view);
                Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");

                sb.Children.Add(fade);
                sb.Children.Add(slide);
                sb.Begin();
            }
            catch { }
        }

        private void OnGaugeAnimationFrame(DispatcherQueueTimer sender, object args)
        {
            try
            {
                const double lerp = 0.16;
                bool changed = false;

                if (Math.Abs(_targetPercent - _displayedPercent) > 0.05)
                {
                    _displayedPercent += (_targetPercent - _displayedPercent) * lerp;
                    _gaugePercentText.Text = $"{_displayedPercent:F1}%";
                    changed = true;
                }
                else if (_displayedPercent != _targetPercent)
                {
                    _displayedPercent = _targetPercent;
                    _gaugePercentText.Text = $"{_displayedPercent:F1}%";
                }

                if (Math.Abs(_targetUsedRatio - _displayedUsedRatio) > 0.0015 ||
                    Math.Abs(_targetCombinedRatio - _displayedCombinedRatio) > 0.0015 ||
                    Math.Abs(_targetThresholdPct - _displayedThresholdPct) > 0.1)
                {
                    _displayedUsedRatio += (_targetUsedRatio - _displayedUsedRatio) * lerp;
                    _displayedCombinedRatio += (_targetCombinedRatio - _displayedCombinedRatio) * lerp;
                    _displayedThresholdPct += (_targetThresholdPct - _displayedThresholdPct) * lerp;
                    changed = true;
                }

                if (changed)
                {
                    UpdateGaugeArc(_gaugeUsedPath, 110, 110, 92, _displayedUsedRatio);
                    UpdateGaugeArc(_gaugeCachePath, 110, 110, 92, _displayedCombinedRatio);
                    UpdateThresholdMarkerDot(110, 110, 92, _displayedThresholdPct);
                }
            }
            catch { }
        }

        #endregion

        #region Win32 Tray Icon & Global Hotkeys

        private void InitializeSystemTrayIcon()
        {
            try
            {
                IntPtr iconHandle = _customHIcon != IntPtr.Zero
                    ? _customHIcon
                    : LoadIcon(IntPtr.Zero, new IntPtr(32512));

                _trayData = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hwnd,
                    uID = 1001,
                    uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                    uCallbackMessage = WM_USER_TRAY,
                    hIcon = iconHandle,
                    szTip = "PulseRAM — WinUI 3 Kernel Memory Optimizer",
                    szInfo = "",
                    szInfoTitle = ""
                };
                _trayInitialized = Shell_NotifyIcon(NIM_ADD, ref _trayData);
            }
            catch { }
        }

        private void UpdateTrayTooltip(double ramPct, double availGb)
        {
            if (!_trayInitialized) return;
            try
            {
                _trayData.uFlags = NIF_TIP;
                _trayData.szTip = $"PulseRAM: {ramPct:F1}% Used ({availGb:F2} GB Available)";
                Shell_NotifyIcon(NIM_MODIFY, ref _trayData);
            }
            catch { }
        }

        private void RegisterGlobalHotkeys()
        {
            try
            {
                UnregisterHotKey(_hwnd, 9001);
                UnregisterHotKey(_hwnd, 9002);
                if (_engine.Config.HotkeysEnabled)
                {
                    RegisterHotKey(_hwnd, 9001, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x4D); // Ctrl+Shift+M
                    RegisterHotKey(_hwnd, 9002, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x50); // Ctrl+Shift+P
                }
            }
            catch { }
        }

        private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == 9001)
                {
                    DispatcherQueue.TryEnqueue(() => RunOptimizationAsync("smart", "Global Hotkey (Ctrl+Shift+M)"));
                }
                else if (id == 9002)
                {
                    DispatcherQueue.TryEnqueue(() => RunOptimizationAsync("deep", "Global Hotkey (Ctrl+Shift+P)"));
                }
            }
            else if (msg == WM_USER_TRAY)
            {
                uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
                if (mouseMsg == WM_LBUTTONDBLCLK)
                {
                    ShowWindow(_hwnd, 9);
                    SetForegroundWindow(_hwnd);
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    ShowTrayContextMenu();
                }
            }
            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private void ShowTrayContextMenu()
        {
            IntPtr hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;
            try
            {
                AppendMenu(hMenu, MF_STRING, new UIntPtr(1), "Open PulseRAM Dashboard");
                AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, "");
                AppendMenu(hMenu, MF_STRING, new UIntPtr(2), "Smart Trim RAM Now (Ctrl+Shift+M)");
                AppendMenu(hMenu, MF_STRING, new UIntPtr(3), "Deep Sweep + Standby Purge (Ctrl+Shift+P)");
                AppendMenu(hMenu, MF_STRING, new UIntPtr(4), _engine.Config.FocusBoostActive ? "Disable Focus Boost Overdrive" : "Enable Focus Boost Overdrive (62%)");
                AppendMenu(hMenu, MF_STRING, new UIntPtr(5), "Thaw All Frozen Processes");
                AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, "");
                AppendMenu(hMenu, MF_STRING, new UIntPtr(6), "Exit PulseRAM");

                GetCursorPos(out POINT pt);
                SetForegroundWindow(_hwnd);
                int cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
                switch (cmd)
                {
                    case 1:
                        ShowWindow(_hwnd, 9);
                        SetForegroundWindow(_hwnd);
                        break;
                    case 2:
                        DispatcherQueue.TryEnqueue(() => RunOptimizationAsync("smart", "System Tray Quick Trim"));
                        break;
                    case 3:
                        DispatcherQueue.TryEnqueue(() => RunOptimizationAsync("deep", "System Tray Deep Sweep"));
                        break;
                    case 4:
                        DispatcherQueue.TryEnqueue(ToggleFocusBoost);
                        break;
                    case 5:
                        DispatcherQueue.TryEnqueue(ThawAllFrozenApps);
                        break;
                    case 6:
                        Close();
                        break;
                }
            }
            finally
            {
                DestroyMenu(hMenu);
            }
        }

        private void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            try
            {
                _uiTimer.Stop();
                _gaugeAnimTimer.Stop();
                UnregisterHotKey(_hwnd, 9001);
                UnregisterHotKey(_hwnd, 9002);
                if (_trayInitialized)
                {
                    Shell_NotifyIcon(NIM_DELETE, ref _trayData);
                }
                _engine.Dispose();
            }
            catch { }
        }

        #endregion

        #region WinUI 3 Visual Tree Construction

        private static SolidColorBrush BrushHex(string hex, byte alpha = 255)
        {
            string h = hex.TrimStart('#');
            byte r = Convert.ToByte(h.Substring(0, 2), 16);
            byte g = Convert.ToByte(h.Substring(2, 2), 16);
            byte b = Convert.ToByte(h.Substring(4, 2), 16);
            return new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
        }

        private static Border CreateCardBorder(Thickness? padding = null)
        {
            return new Border
            {
                Background = BrushHex("#0D1424", 220),
                BorderBrush = BrushHex("#94A3B8", 40),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = padding ?? new Thickness(16)
            };
        }

        private Grid BuildCustomBrandVectorIcon()
        {
            var iconGrid = new Grid { Width = 38, Height = 38 };
            var canvas = new Canvas { Width = 38, Height = 38 };

            // Outer Squircle Background
            var bg = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(10),
                Background = BrushHex("#0A1326"),
                BorderBrush = BrushHex("#38BDF8", 160),
                BorderThickness = new Thickness(1.5)
            };
            iconGrid.Children.Add(bg);

            // Animated Glow Halo
            _logoGlowRing = new Ellipse
            {
                Width = 32,
                Height = 32,
                Stroke = BrushHex("#38BDF8", 190),
                StrokeThickness = 1.5,
                Fill = BrushHex("#0EA5E9", 35)
            };
            Canvas.SetLeft(_logoGlowRing, 3);
            Canvas.SetTop(_logoGlowRing, 3);
            canvas.Children.Add(_logoGlowRing);

            // Silicon RAM IC Pins (Top, Bottom, Left, Right)
            int[] pinOffsets = { 11, 18, 25 };
            foreach (int pos in pinOffsets)
            {
                var pTop = new Rectangle { Width = 2.2, Height = 4, Fill = BrushHex("#38BDF8") };
                Canvas.SetLeft(pTop, pos); Canvas.SetTop(pTop, 4);
                canvas.Children.Add(pTop);

                var pBot = new Rectangle { Width = 2.2, Height = 4, Fill = BrushHex("#38BDF8") };
                Canvas.SetLeft(pBot, pos); Canvas.SetTop(pBot, 30);
                canvas.Children.Add(pBot);

                var pLeft = new Rectangle { Width = 4, Height = 2.2, Fill = BrushHex("#818CF8") };
                Canvas.SetLeft(pLeft, 4); Canvas.SetTop(pLeft, pos);
                canvas.Children.Add(pLeft);

                var pRight = new Rectangle { Width = 4, Height = 2.2, Fill = BrushHex("#818CF8") };
                Canvas.SetLeft(pRight, 30); Canvas.SetTop(pRight, pos);
                canvas.Children.Add(pRight);
            }

            // Inner Silicon Die Core
            var chipCore = new Rectangle
            {
                Width = 20,
                Height = 20,
                RadiusX = 4,
                RadiusY = 4,
                Fill = BrushHex("#091122"),
                Stroke = BrushHex("#38BDF8"),
                StrokeThickness = 1.4
            };
            Canvas.SetLeft(chipCore, 9);
            Canvas.SetTop(chipCore, 9);
            canvas.Children.Add(chipCore);

            // Vector High-Voltage Lightning Bolt inside Core
            var bolt = new Polygon
            {
                Fill = BrushHex("#E0F2FE"),
                Stroke = BrushHex("#38BDF8"),
                StrokeThickness = 0.6,
                Points = new PointCollection
                {
                    new Point(20.5, 11.0),
                    new Point(14.5, 19.5),
                    new Point(18.8, 19.5),
                    new Point(17.2, 27.0),
                    new Point(23.5, 18.2),
                    new Point(19.2, 18.2)
                }
            };
            canvas.Children.Add(bolt);

            iconGrid.Children.Add(canvas);
            return iconGrid;
        }

        private void BuildWinUI3Interface()
        {
            _rootGrid = new Grid
            {
                Background = BrushHex("#060A12", 245),
                Padding = new Thickness(16, 12, 16, 14),
                RowSpacing = 12
            };
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 1. Top Command Header
            var topBar = BuildTopCommandBar();
            Grid.SetRow(topBar, 0);
            _rootGrid.Children.Add(topBar);

            // 2. View Host Container
            var viewHost = new Grid();
            Grid.SetRow(viewHost, 1);

            _viewOverview = BuildOverviewDashboardView();
            _viewProcesses = BuildProcessInspectorView();
            _viewProfiles = BuildProfilesAndHotkeysView();

            _viewProcesses.Visibility = Visibility.Collapsed;
            _viewProfiles.Visibility = Visibility.Collapsed;

            viewHost.Children.Add(_viewOverview);
            viewHost.Children.Add(_viewProcesses);
            viewHost.Children.Add(_viewProfiles);

            // Toast Notification Overlay
            _toastBanner = BuildToastBanner();
            viewHost.Children.Add(_toastBanner);

            _rootGrid.Children.Add(viewHost);
            Content = _rootGrid;
        }

        private Border BuildTopCommandBar()
        {
            var bar = new Border
            {
                Background = BrushHex("#0D1424", 232),
                BorderBrush = BrushHex("#94A3B8", 44),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 9, 14, 9)
            };

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Brand Identity with Custom Vector Icon
            var brandRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, VerticalAlignment = VerticalAlignment.Center };
            brandRow.Children.Add(BuildCustomBrandVectorIcon());

            var titleStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
            titleLine.Children.Add(new TextBlock
            {
                Text = "PulseRAM",
                FontSize = 17.5,
                FontWeight = FontWeights.ExtraBold,
                Foreground = BrushHex("#F8FAFC")
            });
            titleLine.Children.Add(new Border
            {
                Background = BrushHex("#38BDF8", 35),
                BorderBrush = BrushHex("#38BDF8", 110),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(7, 1, 7, 1),
                Child = new TextBlock
                {
                    Text = "WINUI 3 · NT KERNEL",
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushHex("#38BDF8")
                }
            });
            titleStack.Children.Add(titleLine);
            titleStack.Children.Add(new TextBlock
            {
                Text = "Native C# Windows SDK RAM Optimizer & Process Governor",
                FontSize = 11,
                Foreground = BrushHex("#94A3B8")
            });
            brandRow.Children.Add(titleStack);
            Grid.SetColumn(brandRow, 0);
            grid.Children.Add(brandRow);

            // Segmented Navigation Tabs
            var navBorder = new Border
            {
                Background = BrushHex("#060A12", 190),
                BorderBrush = BrushHex("#94A3B8", 35),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var navStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

            _tabBtnOverview = CreateNavTabButton("Dashboard", true, () => SwitchView("overview"));

            var procTabContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            procTabContent.Children.Add(new TextBlock { Text = "Process Inspector", FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
            _tabProcCountText = new TextBlock { Text = "0", FontSize = 11, Foreground = BrushHex("#38BDF8"), FontWeight = FontWeights.Bold };
            procTabContent.Children.Add(_tabProcCountText);
            _tabBtnProcesses = new Button
            {
                Content = procTabContent,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 12, 6)
            };
            _tabBtnProcesses.Click += (s, e) => SwitchView("processes");

            _tabBtnProfiles = CreateNavTabButton("Profiles & Hotkeys", false, () => SwitchView("profiles"));

            navStack.Children.Add(_tabBtnOverview);
            navStack.Children.Add(_tabBtnProcesses);
            navStack.Children.Add(_tabBtnProfiles);
            navBorder.Child = navStack;
            Grid.SetColumn(navBorder, 1);
            grid.Children.Add(navBorder);

            // Right Status Pills & Action Buttons
            var rightStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            _focusBoostBtn = new Button
            {
                Content = "⚡ Focus Boost: OFF",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(12, 5, 12, 5),
                Background = BrushHex("#151F35", 190),
                BorderBrush = BrushHex("#94A3B8", 45),
                BorderThickness = new Thickness(1)
            };
            _focusBoostBtn.Click += (s, e) => ToggleFocusBoost();
            rightStack.Children.Add(_focusBoostBtn);

            // Active Auto-Pilot Pill
            var apPill = new Border
            {
                Background = BrushHex("#151F35", 190),
                BorderBrush = BrushHex("#94A3B8", 40),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(11, 5, 11, 5),
                VerticalAlignment = VerticalAlignment.Center
            };
            var apRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
            _apStatusDot = new Ellipse { Width = 8, Height = 8, Fill = BrushHex("#10B981") };
            _apStatusPillText = new TextBlock { Text = "Auto-Pilot: 80%", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#F8FAFC") };
            apRow.Children.Add(_apStatusDot);
            apRow.Children.Add(_apStatusPillText);
            apPill.Child = apRow;
            rightStack.Children.Add(apPill);

            // Session Reclaimed Pill
            var freedPill = new Border
            {
                Background = BrushHex("#151F35", 190),
                BorderBrush = BrushHex("#10B981", 65),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(11, 5, 11, 5),
                VerticalAlignment = VerticalAlignment.Center
            };
            var freedRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            freedRow.Children.Add(new TextBlock { Text = "Freed:", FontSize = 11.5, Foreground = BrushHex("#94A3B8") });
            _sessionFreedTopText = new TextBlock { Text = "0 MB", FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = BrushHex("#34D399") };
            freedRow.Children.Add(_sessionFreedTopText);
            freedPill.Child = freedRow;
            rightStack.Children.Add(freedPill);

            // Mini HUD Toggle
            _miniHudBtn = new Button
            {
                Content = "Mini HUD",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(11, 5, 11, 5),
                Background = BrushHex("#151F35", 190),
                BorderBrush = BrushHex("#94A3B8", 45),
                BorderThickness = new Thickness(1)
            };
            _miniHudBtn.Click += (s, e) => ToggleMiniHudMode();
            rightStack.Children.Add(_miniHudBtn);

            // Admin Button
            _adminBtn = new Button
            {
                Content = "Unlock Kernel Admin",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(12, 5, 12, 5),
                Background = BrushHex("#F59E0B", 38),
                BorderBrush = BrushHex("#F59E0B", 110),
                Foreground = BrushHex("#FBBF24"),
                BorderThickness = new Thickness(1)
            };
            _adminBtn.Click += (s, e) => RequestAdminElevation();
            rightStack.Children.Add(_adminBtn);

            Grid.SetColumn(rightStack, 3);
            grid.Children.Add(rightStack);

            bar.Child = grid;
            return bar;
        }

        private static Button CreateNavTabButton(string label, bool active, Action onClick)
        {
            var btn = new Button
            {
                Content = label,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(13, 6, 13, 6),
                Background = active ? BrushHex("#38BDF8", 45) : new SolidColorBrush(Colors.Transparent),
                BorderBrush = active ? BrushHex("#38BDF8", 120) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                Foreground = BrushHex(active ? "#F8FAFC" : "#94A3B8")
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private Grid BuildOverviewDashboardView()
        {
            var outer = new Grid();
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var mainStack = new StackPanel
            {
                Spacing = 12,
                Padding = new Thickness(0, 0, 4, 8),
                ChildrenTransitions = CreateFluentTransitions()
            };

            // Top Split: Left Reactor (375px) | Right Telemetry + Auto-Pilot (*)
            var topGrid = new Grid { ColumnSpacing = 12 };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(375) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var reactorCard = BuildMemoryReactorCard();
            Grid.SetColumn(reactorCard, 0);
            topGrid.Children.Add(reactorCard);

            _rightColumnOverview = new Grid { RowSpacing = 12, ChildrenTransitions = CreateFluentTransitions() };
            _rightColumnOverview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _rightColumnOverview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var telemetryCard = BuildTelemetryAndVitalsCard();
            Grid.SetRow(telemetryCard, 0);
            _rightColumnOverview.Children.Add(telemetryCard);

            var autoPilotCard = BuildAutoPilotAndGuardsCard();
            Grid.SetRow(autoPilotCard, 1);
            _rightColumnOverview.Children.Add(autoPilotCard);

            Grid.SetColumn(_rightColumnOverview, 1);
            topGrid.Children.Add(_rightColumnOverview);

            mainStack.Children.Add(topGrid);

            // Bottom Split: Top 8 Memory Consumers (1.28*) | Optimization Activity Log (1*)
            _bottomSplitOverview = new Grid { ColumnSpacing = 12 };
            _bottomSplitOverview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.28, GridUnitType.Star) });
            _bottomSplitOverview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var topConsumersCard = BuildDashboardTopConsumersCard();
            Grid.SetColumn(topConsumersCard, 0);
            _bottomSplitOverview.Children.Add(topConsumersCard);

            var activityLogCard = BuildActivityLogCard();
            Grid.SetColumn(activityLogCard, 1);
            _bottomSplitOverview.Children.Add(activityLogCard);

            mainStack.Children.Add(_bottomSplitOverview);

            scroll.Content = mainStack;
            outer.Children.Add(scroll);
            return outer;
        }

        private Border BuildMemoryReactorCard()
        {
            var card = CreateCardBorder();
            var stack = new StackPanel { Spacing = 12 };

            // Header
            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.Children.Add(new TextBlock
            {
                Text = "MEMORY REACTOR",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#38BDF8")
            });
            _clockText = new TextBlock
            {
                Text = DateTime.Now.ToString("HH:mm:ss"),
                FontSize = 11.5,
                Foreground = BrushHex("#94A3B8")
            };
            Grid.SetColumn(_clockText, 1);
            hdr.Children.Add(_clockText);
            stack.Children.Add(hdr);

            // Radial Gauge Canvas (220x220) with Animated Halo, Orbital Ring, & Shockwave
            var gaugeHost = new Grid { Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Center };
            _gaugeCanvas = new Canvas { Width = 220, Height = 220 };

            // 1. Ambient Breathing Halo
            _reactorPulseHalo = new Ellipse
            {
                Width = 206,
                Height = 206,
                Stroke = BrushHex("#38BDF8", 95),
                StrokeThickness = 2,
                Fill = BrushHex("#0EA5E9", 18),
                Opacity = 0.35
            };
            Canvas.SetLeft(_reactorPulseHalo, 7);
            Canvas.SetTop(_reactorPulseHalo, 7);
            _gaugeCanvas.Children.Add(_reactorPulseHalo);

            // 2. Continuous 360 Rotating Dashed Orbital Ring
            _reactorOrbitalRing = new Ellipse
            {
                Width = 214,
                Height = 214,
                Stroke = BrushHex("#38BDF8", 90),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 6, 8 },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new CompositeTransform()
            };
            Canvas.SetLeft(_reactorOrbitalRing, 3);
            Canvas.SetTop(_reactorOrbitalRing, 3);
            _gaugeCanvas.Children.Add(_reactorOrbitalRing);

            // 3. Optimization Shockwave Burst Ring
            _reactorShockwaveRing = new Ellipse
            {
                Width = 184,
                Height = 184,
                Stroke = BrushHex("#34D399"),
                StrokeThickness = 4,
                Opacity = 0.0,
                Visibility = Visibility.Collapsed,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new CompositeTransform()
            };
            Canvas.SetLeft(_reactorShockwaveRing, 18);
            Canvas.SetTop(_reactorShockwaveRing, 18);
            _gaugeCanvas.Children.Add(_reactorShockwaveRing);

            // 4. Outer Ring Track
            var bgRing = new Ellipse
            {
                Width = 184,
                Height = 184,
                Stroke = BrushHex("#1E293B", 230),
                StrokeThickness = 14
            };
            Canvas.SetLeft(bgRing, 18);
            Canvas.SetTop(bgRing, 18);
            _gaugeCanvas.Children.Add(bgRing);

            _gaugeCachePath = new Microsoft.UI.Xaml.Shapes.Path
            {
                Stroke = BrushHex("#6366F1", 125),
                StrokeThickness = 14,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _gaugeCanvas.Children.Add(_gaugeCachePath);

            _gaugeUsedPath = new Microsoft.UI.Xaml.Shapes.Path
            {
                Stroke = BrushHex("#10B981"),
                StrokeThickness = 14,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _gaugeCanvas.Children.Add(_gaugeUsedPath);

            _gaugeThresholdDot = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = BrushHex("#F43F5E"),
                Stroke = BrushHex("#FFFFFF"),
                StrokeThickness = 2
            };
            _gaugeCanvas.Children.Add(_gaugeThresholdDot);

            gaugeHost.Children.Add(_gaugeCanvas);

            // Center Gauge Labels
            var centerLabels = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2
            };
            _gaugePercentText = new TextBlock
            {
                Text = "0.0%",
                FontSize = 36,
                FontWeight = FontWeights.ExtraBold,
                Foreground = BrushHex("#F8FAFC"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _gaugeSubText = new TextBlock
            {
                Text = "0.00 / 0.00 GB",
                FontSize = 12,
                Foreground = BrushHex("#94A3B8"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _pressureBadgeBorder = new Border
            {
                Background = BrushHex("#10B981", 38),
                BorderBrush = BrushHex("#10B981", 110),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _pressureBadgeText = new TextBlock
            {
                Text = "OPTIMAL LOAD",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#10B981")
            };
            _pressureBadgeBorder.Child = _pressureBadgeText;

            centerLabels.Children.Add(_gaugePercentText);
            centerLabels.Children.Add(_gaugeSubText);
            centerLabels.Children.Add(_pressureBadgeBorder);
            gaugeHost.Children.Add(centerLabels);
            stack.Children.Add(gaugeHost);

            // Physical Memory Map Box
            var mapBox = new Border
            {
                Background = BrushHex("#060A12", 165),
                BorderBrush = BrushHex("#94A3B8", 32),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var mapStack = new StackPanel { Spacing = 7 };
            var mapHdr = new Grid();
            mapHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mapHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mapHdr.Children.Add(new TextBlock { Text = "Physical Memory Map", FontSize = 11, Foreground = BrushHex("#94A3B8") });
            _availSummaryText = new TextBlock { Text = "-- GB Available", FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = BrushHex("#F8FAFC") };
            Grid.SetColumn(_availSummaryText, 1);
            mapHdr.Children.Add(_availSummaryText);
            mapStack.Children.Add(mapHdr);

            var barGrid = new Grid { Height = 9, ColumnSpacing = 2 };
            _colSegUsed = new ColumnDefinition { Width = new GridLength(50, GridUnitType.Star) };
            _colSegCache = new ColumnDefinition { Width = new GridLength(25, GridUnitType.Star) };
            _colSegFree = new ColumnDefinition { Width = new GridLength(25, GridUnitType.Star) };
            barGrid.ColumnDefinitions.Add(_colSegUsed);
            barGrid.ColumnDefinitions.Add(_colSegCache);
            barGrid.ColumnDefinitions.Add(_colSegFree);

            var seg1 = new Border { Background = BrushHex("#38BDF8"), CornerRadius = new CornerRadius(4, 0, 0, 4) };
            var seg2 = new Border { Background = BrushHex("#818CF8") };
            var seg3 = new Border { Background = BrushHex("#10B981", 110), CornerRadius = new CornerRadius(0, 4, 4, 0) };
            Grid.SetColumn(seg1, 0);
            Grid.SetColumn(seg2, 1);
            Grid.SetColumn(seg3, 2);
            barGrid.Children.Add(seg1);
            barGrid.Children.Add(seg2);
            barGrid.Children.Add(seg3);
            mapStack.Children.Add(barGrid);

            var legGrid = new Grid { ColumnSpacing = 6 };
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            legGrid.Children.Add(CreateLegendCell("In Use", "#38BDF8", out _legUsedText, 0));
            legGrid.Children.Add(CreateLegendCell("Standby Cache", "#818CF8", out _legCacheText, 1));
            legGrid.Children.Add(CreateLegendCell("Available", "#10B981", out _legAvailText, 2));
            mapStack.Children.Add(legGrid);
            mapBox.Child = mapStack;
            stack.Children.Add(mapBox);

            // 4 Cleaning Mode Selector Cards (2x2 Grid)
            stack.Children.Add(new TextBlock
            {
                Text = "CLEANING INTELLIGENCE MODE",
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#94A3B8")
            });

            var modeGrid = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
            modeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            modeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddModeCard(modeGrid, "smart", "Smart Trim", "Safe", "Idle background bloat only.", 0, 0);
            AddModeCard(modeGrid, "deep", "Deep Sweep", "Thorough", "All eligible working sets.", 0, 1);
            AddModeCard(modeGrid, "standby", "Standby Purge", "Kernel", "Flushes OS Standby Cache.", 1, 0);
            AddModeCard(modeGrid, "max", "Max Overdrive", "Extreme", "Full RAM + Page Combine.", 1, 1);
            stack.Children.Add(modeGrid);

            // Primary Optimize Now Button
            var ctaStack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            _optimizeBtnLabel = new TextBlock
            {
                Text = "⚡ Smart Trim RAM Now",
                FontSize = 14.5,
                FontWeight = FontWeights.ExtraBold,
                Foreground = BrushHex("#FFFFFF"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _optimizeBtnSub = new TextBlock
            {
                Text = "Est. Reclaimable: -- MB",
                FontSize = 11,
                Foreground = BrushHex("#E0F2FE"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ctaStack.Children.Add(_optimizeBtnLabel);
            ctaStack.Children.Add(_optimizeBtnSub);

            _optimizeNowBtn = new Button
            {
                Content = ctaStack,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(14, 10, 14, 10),
                CornerRadius = new CornerRadius(12),
                Background = BrushHex("#0284C7"),
                BorderBrush = BrushHex("#38BDF8"),
                BorderThickness = new Thickness(1)
            };
            _optimizeNowBtn.Click += (s, e) => RunOptimizationAsync(_selectedMode, $"Manual {_selectedMode.ToUpperInvariant()} optimization");
            stack.Children.Add(_optimizeNowBtn);

            // Last Sweep Summary Bar
            var lastSweepBox = new Border
            {
                Background = BrushHex("#060A12", 160),
                BorderBrush = BrushHex("#94A3B8", 30),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 7, 10, 7)
            };
            _lastSweepSummaryText = new TextBlock
            {
                Text = "Last Sweep: Ready — Auto-Pilot armed & monitoring",
                FontSize = 11,
                Foreground = BrushHex("#94A3B8"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            lastSweepBox.Child = _lastSweepSummaryText;
            stack.Children.Add(lastSweepBox);

            card.Child = stack;
            return card;
        }

        private static StackPanel CreateLegendCell(string title, string dotHex, out TextBlock valBlock, int col)
        {
            var sp = new StackPanel { Spacing = 1 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), Background = BrushHex(dotHex) });
            row.Children.Add(new TextBlock { Text = title, FontSize = 10, Foreground = BrushHex("#94A3B8") });
            valBlock = new TextBlock { Text = "-- GB", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = BrushHex("#F8FAFC") };
            sp.Children.Add(row);
            sp.Children.Add(valBlock);
            Grid.SetColumn(sp, col);
            return sp;
        }

        private void AddModeCard(Grid parent, string modeKey, string title, string tag, string desc, int row, int col)
        {
            bool isSelected = modeKey == _selectedMode;
            var border = new Border
            {
                Background = isSelected ? BrushHex("#0EA5E9", 42) : BrushHex("#151F35", 150),
                BorderBrush = isSelected ? BrushHex("#38BDF8") : BrushHex("#94A3B8", 35),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8)
            };

            var sp = new StackPanel { Spacing = 2 };
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = BrushHex("#F8FAFC") });
            var tagTxt = new TextBlock { Text = tag.ToUpperInvariant(), FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = BrushHex("#38BDF8") };
            Grid.SetColumn(tagTxt, 1);
            top.Children.Add(tagTxt);

            sp.Children.Add(top);
            sp.Children.Add(new TextBlock { Text = desc, FontSize = 10.5, Foreground = BrushHex("#94A3B8"), TextWrapping = TextWrapping.Wrap });
            border.Child = sp;

            border.PointerPressed += (s, e) => SelectCleaningMode(modeKey);
            _modeCards[modeKey] = border;

            Grid.SetRow(border, row);
            Grid.SetColumn(border, col);
            parent.Children.Add(border);
        }

        private Border BuildTelemetryAndVitalsCard()
        {
            var card = CreateCardBorder();
            var stack = new StackPanel { Spacing = 10 };

            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.Children.Add(new TextBlock
            {
                Text = "60-SECOND REAL-TIME MEMORY & CPU TELEMETRY",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#38BDF8")
            });
            _chartMetaText = new TextBlock
            {
                Text = "Cyan: RAM % · Green: CPU % · Red Line: Trigger Ceiling",
                FontSize = 11,
                Foreground = BrushHex("#94A3B8")
            };
            Grid.SetColumn(_chartMetaText, 1);
            hdr.Children.Add(_chartMetaText);
            stack.Children.Add(hdr);

            // Chart Box
            var chartBorder = new Border
            {
                Background = BrushHex("#060A12", 190),
                BorderBrush = BrushHex("#94A3B8", 32),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Height = 140,
                Padding = new Thickness(6)
            };
            _chartCanvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            _chartCanvas.SizeChanged += (s, e) => DrawTelemetryChart(_engine.GetTelemetry(), _engine.GetSnapshot().EffectiveThresholdPercent);
            chartBorder.Child = _chartCanvas;
            stack.Children.Add(chartBorder);

            // 6-Card Kernel Vitals Grid (3 columns x 2 rows)
            var vitalsGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8, ChildrenTransitions = CreateFluentTransitions() };
            for (int c = 0; c < 3; c++) vitalsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            vitalsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            vitalsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            vitalsGrid.Children.Add(CreateVitalCard("SESSION RECLAIMED", "#34D399", out _vitReclaimedVal, out _vitReclaimedSub, 0, 0));
            vitalsGrid.Children.Add(CreateVitalCard("COMMIT CHARGE", "#F8FAFC", out _vitCommitVal, out _vitCommitSub, 0, 1));
            vitalsGrid.Children.Add(CreateVitalCard("PAGEFILE / SWAP", "#F8FAFC", out _vitPagefileVal, out _vitPagefileSub, 0, 2));
            vitalsGrid.Children.Add(CreateVitalCard("KERNEL POOLS", "#F8FAFC", out _vitKernelVal, out _vitKernelSub, 1, 0));
            vitalsGrid.Children.Add(CreateVitalCard("CPU & THREADS", "#38BDF8", out _vitCpuVal, out _vitCpuSub, 1, 1));
            vitalsGrid.Children.Add(CreateVitalCard("GOVERNOR STATUS", "#A855F7", out _vitGovVal, out _vitGovSub, 1, 2));

            stack.Children.Add(vitalsGrid);
            card.Child = stack;
            return card;
        }

        private static Border CreateVitalCard(string label, string valColorHex, out TextBlock valBlock, out TextBlock subBlock, int row, int col)
        {
            var b = new Border
            {
                Background = BrushHex("#060A12", 165),
                BorderBrush = BrushHex("#94A3B8", 30),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 8, 11, 8)
            };
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = BrushHex("#64748B") });
            valBlock = new TextBlock { Text = "--", FontSize = 15.5, FontWeight = FontWeights.ExtraBold, Foreground = BrushHex(valColorHex) };
            subBlock = new TextBlock { Text = "--", FontSize = 10.5, Foreground = BrushHex("#94A3B8"), TextTrimming = TextTrimming.CharacterEllipsis };
            sp.Children.Add(valBlock);
            sp.Children.Add(subBlock);
            b.Child = sp;
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            return b;
        }

        private Border BuildAutoPilotAndGuardsCard()
        {
            var card = CreateCardBorder();
            var stack = new StackPanel { Spacing = 10 };

            // Header with Master Auto-Pilot Switch
            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleStack = new StackPanel { Spacing = 1 };
            titleStack.Children.Add(new TextBlock
            {
                Text = "INTELLIGENT AUTO-PILOT & ACTIVE GUARDS",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#38BDF8")
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "Automatically trims background RAM bloat when thresholds are crossed",
                FontSize = 11,
                Foreground = BrushHex("#94A3B8")
            });
            hdr.Children.Add(titleStack);

            _swAutoPilot = new ToggleSwitch
            {
                OnContent = "Auto-Pilot Armed",
                OffContent = "Paused",
                IsOn = _engine.Config.AutoEnabled
            };
            _swAutoPilot.Toggled += (s, e) =>
            {
                if (_isUpdatingUi) return;
                _engine.Config.AutoEnabled = _swAutoPilot.IsOn;
                _engine.SaveConfig();
                RefreshAllUi();
            };
            Grid.SetColumn(_swAutoPilot, 1);
            hdr.Children.Add(_swAutoPilot);
            stack.Children.Add(hdr);

            // Sliders Row (Threshold % & Min Free GB)
            var slidersGrid = new Grid { ColumnSpacing = 12 };
            slidersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            slidersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Slider 1: RAM % Threshold
            var s1Box = new Border
            {
                Background = BrushHex("#060A12", 160),
                BorderBrush = BrushHex("#94A3B8", 30),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8)
            };
            var s1Stack = new StackPanel { Spacing = 4 };
            var s1Top = new Grid();
            s1Top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            s1Top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            s1Top.Children.Add(new TextBlock { Text = "RAM Trigger Threshold", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#F8FAFC") });
            _lblThresholdVal = new TextBlock { Text = "80%", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = BrushHex("#38BDF8") };
            Grid.SetColumn(_lblThresholdVal, 1);
            s1Top.Children.Add(_lblThresholdVal);
            s1Stack.Children.Add(s1Top);

            _sliderThreshold = new Slider { Minimum = 50, Maximum = 95, StepFrequency = 1, Value = _engine.Config.RamThresholdPercent };
            _sliderThreshold.ValueChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                int v = (int)Math.Round(e.NewValue);
                _lblThresholdVal.Text = $"{v}%";
                _engine.Config.RamThresholdPercent = v;
                _targetThresholdPct = v;
                _engine.SaveConfig();
            };
            s1Stack.Children.Add(_sliderThreshold);
            s1Box.Child = s1Stack;
            Grid.SetColumn(s1Box, 0);
            slidersGrid.Children.Add(s1Box);

            // Slider 2: Min Free RAM Floor
            var s2Box = new Border
            {
                Background = BrushHex("#060A12", 160),
                BorderBrush = BrushHex("#94A3B8", 30),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8)
            };
            var s2Stack = new StackPanel { Spacing = 4 };
            var s2Top = new Grid();
            s2Top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            s2Top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            s2Top.Children.Add(new TextBlock { Text = "Min Free RAM Emergency Floor", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#F8FAFC") });
            _lblMinFreeVal = new TextBlock { Text = "2.0 GB", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = BrushHex("#38BDF8") };
            Grid.SetColumn(_lblMinFreeVal, 1);
            s2Top.Children.Add(_lblMinFreeVal);
            s2Stack.Children.Add(s2Top);

            _sliderMinFree = new Slider { Minimum = 0.5, Maximum = 8.0, StepFrequency = 0.5, Value = _engine.Config.MinFreeRamGb };
            _sliderMinFree.ValueChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                double v = Math.Round(e.NewValue * 2.0) / 2.0;
                _lblMinFreeVal.Text = $"{v:F1} GB";
                _engine.Config.MinFreeRamGb = v;
                _engine.SaveConfig();
            };
            s2Stack.Children.Add(_sliderMinFree);
            s2Box.Child = s2Stack;
            Grid.SetColumn(s2Box, 1);
            slidersGrid.Children.Add(s2Box);

            stack.Children.Add(slidersGrid);

            // Presets + Mode & Cooldown Row
            var optsGrid = new Grid { ColumnSpacing = 10 };
            optsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            optsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            optsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });

            var presetStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            presetStack.Children.Add(CreateQuickPresetBtn("70% Gaming", 70, 3.0, "smart"));
            presetStack.Children.Add(CreateQuickPresetBtn("80% Balanced", 80, 2.0, "smart"));
            presetStack.Children.Add(CreateQuickPresetBtn("88% Heavy", 88, 1.5, "deep"));
            Grid.SetColumn(presetStack, 0);
            optsGrid.Children.Add(presetStack);

            _cmbAutoMode = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            _cmbAutoMode.Items.Add(new ComboBoxItem { Content = "Auto Mode: Smart Trim", Tag = "smart" });
            _cmbAutoMode.Items.Add(new ComboBoxItem { Content = "Auto Mode: Deep Sweep", Tag = "deep" });
            _cmbAutoMode.Items.Add(new ComboBoxItem { Content = "Auto Mode: Standby Purge", Tag = "standby" });
            _cmbAutoMode.Items.Add(new ComboBoxItem { Content = "Auto Mode: Max Overdrive", Tag = "max" });
            _cmbAutoMode.SelectedIndex = 0;
            _cmbAutoMode.SelectionChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                if (_cmbAutoMode.SelectedItem is ComboBoxItem item && item.Tag is string m)
                {
                    _engine.Config.AutoMode = m;
                    _engine.SaveConfig();
                }
            };
            Grid.SetColumn(_cmbAutoMode, 1);
            optsGrid.Children.Add(_cmbAutoMode);

            _cmbCooldown = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            _cmbCooldown.Items.Add(new ComboBoxItem { Content = "Cooldown: 15s", Tag = 15 });
            _cmbCooldown.Items.Add(new ComboBoxItem { Content = "Cooldown: 30s", Tag = 30 });
            _cmbCooldown.Items.Add(new ComboBoxItem { Content = "Cooldown: 60s", Tag = 60 });
            _cmbCooldown.Items.Add(new ComboBoxItem { Content = "Cooldown: 120s", Tag = 120 });
            _cmbCooldown.SelectedIndex = 1;
            _cmbCooldown.SelectionChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                if (_cmbCooldown.SelectedItem is ComboBoxItem item && item.Tag is int cd)
                {
                    _engine.Config.CooldownSeconds = cd;
                    _engine.SaveConfig();
                }
            };
            Grid.SetColumn(_cmbCooldown, 2);
            optsGrid.Children.Add(_cmbCooldown);
            stack.Children.Add(optsGrid);

            // 4 Compact Guard Tiles (2x2 Grid)
            var guardsGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            guardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            guardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            guardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            guardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Guard 1: Foreground App Shield + Audio Chime
            var g1 = CreateGuardTile("Foreground App Shield", "Never trims active window", out _swProtectFg, _engine.Config.ProtectForeground, val =>
            {
                _engine.Config.ProtectForeground = val;
                _engine.SaveConfig();
            });
            _chkSoundChime = new CheckBox
            {
                Content = "Chime",
                IsChecked = _engine.Config.AutoSoundChime,
                FontSize = 11,
                MinWidth = 0,
                Padding = new Thickness(5, 0, 0, 0),
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _chkSoundChime.Click += (s, e) =>
            {
                _engine.Config.AutoSoundChime = _chkSoundChime.IsChecked == true;
                _engine.SaveConfig();
            };
            Grid.SetColumn(_chkSoundChime, 1);
            ((Grid)g1.Child).Children.Add(_chkSoundChime);
            Grid.SetRow(g1, 0);
            Grid.SetColumn(g1, 0);
            guardsGrid.Children.Add(g1);

            // Guard 2: Runaway Leak Guardian
            var g2 = CreateGuardTile("Runaway Leak Guardian", "Auto-trims +120 MB/min leaks", out _swLeakGuard, _engine.Config.AutoLeakRemediation, val =>
            {
                _engine.Config.AutoLeakRemediation = val;
                _engine.SaveConfig();
            });
            Grid.SetRow(g2, 0);
            Grid.SetColumn(g2, 1);
            guardsGrid.Children.Add(g2);

            // Guard 3: Scheduled Maintenance Timer
            var g3 = new Border
            {
                Background = BrushHex("#060A12", 160),
                BorderBrush = BrushHex("#94A3B8", 30),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 7, 11, 7)
            };
            var g3Grid = new Grid { ColumnSpacing = 8 };
            g3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var g3Text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            g3Text.Children.Add(new TextBlock { Text = "Scheduled Sweep", FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = BrushHex("#F8FAFC") });
            _lblSchedCountdown = new TextBlock { Text = "Periodic timer off", FontSize = 10.5, Foreground = BrushHex("#94A3B8") };
            g3Text.Children.Add(_lblSchedCountdown);
            Grid.SetColumn(g3Text, 0);
            g3Grid.Children.Add(g3Text);

            _cmbSchedInterval = new ComboBox { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            _cmbSchedInterval.Items.Add(new ComboBoxItem { Content = "10m", Tag = 10 });
            _cmbSchedInterval.Items.Add(new ComboBoxItem { Content = "15m", Tag = 15 });
            _cmbSchedInterval.Items.Add(new ComboBoxItem { Content = "30m", Tag = 30 });
            _cmbSchedInterval.Items.Add(new ComboBoxItem { Content = "60m", Tag = 60 });
            _cmbSchedInterval.SelectedIndex = 1;
            _cmbSchedInterval.SelectionChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                if (_cmbSchedInterval.SelectedItem is ComboBoxItem item && item.Tag is int mins)
                {
                    _engine.Config.ScheduledIntervalMinutes = mins;
                    _engine.SaveConfig();
                }
            };
            Grid.SetColumn(_cmbSchedInterval, 1);
            g3Grid.Children.Add(_cmbSchedInterval);

            _swScheduled = new ToggleSwitch
            {
                OnContent = null,
                OffContent = null,
                MinWidth = 0,
                Width = 44,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                IsOn = _engine.Config.ScheduledTrimEnabled
            };
            _swScheduled.Toggled += (s, e) =>
            {
                if (_isUpdatingUi) return;
                _engine.Config.ScheduledTrimEnabled = _swScheduled.IsOn;
                _engine.SaveConfig();
                RefreshAllUi();
            };
            Grid.SetColumn(_swScheduled, 2);
            g3Grid.Children.Add(_swScheduled);
            g3.Child = g3Grid;
            Grid.SetRow(g3, 1);
            Grid.SetColumn(g3, 0);
            guardsGrid.Children.Add(g3);

            // Guard 4: Windows Startup Registry
            var g4 = CreateGuardTile("Windows Startup", "Auto-start in System Tray", out _swStartup, _engine.Config.RunOnStartup, val =>
            {
                _engine.SetWindowsStartupRegistry(val);
                ShowToast(val ? "Windows Startup Enabled" : "Windows Startup Disabled",
                    val ? "Registered PulseRAM (--tray) in HKCU\\...\\Run" : "Removed from Windows Startup registry");
            });
            Grid.SetRow(g4, 1);
            Grid.SetColumn(g4, 1);
            guardsGrid.Children.Add(g4);

            stack.Children.Add(guardsGrid);
            card.Child = stack;
            return card;
        }

        private Button CreateQuickPresetBtn(string label, int pct, double minFree, string mode)
        {
            var btn = new Button
            {
                Content = label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(9, 5, 9, 5),
                CornerRadius = new CornerRadius(8),
                Background = BrushHex("#151F35", 180),
                BorderBrush = BrushHex("#94A3B8", 40),
                BorderThickness = new Thickness(1)
            };
            btn.Click += (s, e) =>
            {
                _engine.Config.RamThresholdPercent = pct;
                _engine.Config.MinFreeRamGb = minFree;
                _engine.Config.AutoMode = mode;
                _engine.SaveConfig();
                RefreshAllUi();
                ShowToast($"Applied {label} Preset", $"Auto-Pilot set to {pct}% RAM ceiling ({minFree:F1} GB floor)");
            };
            return btn;
        }

        private Border CreateGuardTile(string title, string sub, out ToggleSwitch toggle, bool initialValue, Action<bool> onChanged)
        {
            var b = new Border
            {
                Background = BrushHex("#060A12", 160),
                BorderBrush = BrushHex("#94A3B8", 30),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 7, 11, 7)
            };
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var sp = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = BrushHex("#F8FAFC") });
            sp.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = BrushHex("#94A3B8") });
            Grid.SetColumn(sp, 0);
            g.Children.Add(sp);

            var sw = new ToggleSwitch
            {
                OnContent = null,
                OffContent = null,
                MinWidth = 0,
                Width = 44,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                IsOn = initialValue
            };
            sw.Toggled += (s, e) =>
            {
                if (_isUpdatingUi) return;
                onChanged(sw.IsOn);
            };
            Grid.SetColumn(sw, 2);
            g.Children.Add(sw);
            toggle = sw;
            b.Child = g;
            return b;
        }

        private Border BuildDashboardTopConsumersCard()
        {
            var card = CreateCardBorder();
            var stack = new StackPanel { Spacing = 10 };

            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Spacing = 1 };
            titleStack.Children.Add(new TextBlock
            {
                Text = "TOP MEMORY CONSUMERS (QUICK GOVERNOR)",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#38BDF8")
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "Instant per-app Working Set Trim, EcoQoS, Cryo-Freeze, Cap, & Shield",
                FontSize = 11,
                Foreground = BrushHex("#94A3B8")
            });
            hdr.Children.Add(titleStack);

            var openAllBtn = new Button
            {
                Content = "Open Full Inspector →",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(10, 5, 10, 5),
                CornerRadius = new CornerRadius(8),
                Background = BrushHex("#151F35", 180),
                BorderBrush = BrushHex("#38BDF8", 90),
                Foreground = BrushHex("#38BDF8"),
                BorderThickness = new Thickness(1)
            };
            openAllBtn.Click += (s, e) => SwitchView("processes");
            Grid.SetColumn(openAllBtn, 1);
            hdr.Children.Add(openAllBtn);
            stack.Children.Add(hdr);

            var scroll = new ScrollViewer { MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _dashTopProcsPanel = new StackPanel { Spacing = 6 };
            scroll.Content = _dashTopProcsPanel;
            stack.Children.Add(scroll);

            card.Child = stack;
            return card;
        }

        private Border BuildActivityLogCard()
        {
            var card = CreateCardBorder();
            var stack = new StackPanel { Spacing = 10 };

            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.Children.Add(new TextBlock
            {
                Text = "OPTIMIZATION ACTIVITY LOG",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#38BDF8")
            });
            _eventCountText = new TextBlock { Text = "0 sweeps", FontSize = 11, Foreground = BrushHex("#94A3B8") };
            Grid.SetColumn(_eventCountText, 1);
            hdr.Children.Add(_eventCountText);
            stack.Children.Add(hdr);

            var scroll = new ScrollViewer { MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _eventLogPanel = new StackPanel { Spacing = 6 };
            scroll.Content = _eventLogPanel;
            stack.Children.Add(scroll);

            card.Child = stack;
            return card;
        }

        private Grid BuildProcessInspectorView()
        {
            var outer = new Grid();
            var card = CreateCardBorder();
            var stack = new StackPanel { Spacing = 12 };

            // Toolbar
            var toolbar = new Grid { ColumnSpacing = 8 };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _searchBox = new TextBox
            {
                PlaceholderText = "Search process or PID...",
                FontSize = 12
            };
            _searchBox.TextChanged += (s, e) =>
            {
                _procSearchQuery = _searchBox.Text.Trim();
                RenderProcessLists(_engine.GetProcesses(_groupedProcesses));
            };
            Grid.SetColumn(_searchBox, 0);
            toolbar.Children.Add(_searchBox);

            _cmbSortProcs = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            _cmbSortProcs.Items.Add(new ComboBoxItem { Content = "Sort: Memory (MB)", Tag = "memory" });
            _cmbSortProcs.Items.Add(new ComboBoxItem { Content = "Sort: Smart Score", Tag = "score" });
            _cmbSortProcs.Items.Add(new ComboBoxItem { Content = "Sort: CPU %", Tag = "cpu" });
            _cmbSortProcs.Items.Add(new ComboBoxItem { Content = "Sort: Leak (+MB/m)", Tag = "leak" });
            _cmbSortProcs.SelectedIndex = 0;
            _cmbSortProcs.SelectionChanged += (s, e) =>
            {
                if (_cmbSortProcs.SelectedItem is ComboBoxItem item && item.Tag is string sortKey)
                {
                    _procSort = sortKey;
                    RenderProcessLists(_engine.GetProcesses(_groupedProcesses));
                }
            };
            Grid.SetColumn(_cmbSortProcs, 1);
            toolbar.Children.Add(_cmbSortProcs);

            // Grouped vs Individual PIDs Toggle
            var grpStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            _btnGroupApps = new Button
            {
                Content = "Grouped Apps",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                CornerRadius = new CornerRadius(8),
                Background = BrushHex("#38BDF8", 45),
                BorderBrush = BrushHex("#38BDF8"),
                BorderThickness = new Thickness(1)
            };
            _btnGroupPids = new Button
            {
                Content = "Individual PIDs",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                CornerRadius = new CornerRadius(8),
                Background = BrushHex("#151F35", 180),
                BorderBrush = BrushHex("#94A3B8", 40),
                BorderThickness = new Thickness(1)
            };
            _btnGroupApps.Click += (s, e) => SetProcessGrouping(true);
            _btnGroupPids.Click += (s, e) => SetProcessGrouping(false);
            grpStack.Children.Add(_btnGroupApps);
            grpStack.Children.Add(_btnGroupPids);
            Grid.SetColumn(grpStack, 2);
            toolbar.Children.Add(grpStack);

            // Filter Buttons + Thaw All
            var filterStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right };
            AddFilterButton(filterStack, "all", "All Apps");
            AddFilterButton(filterStack, "idle", "Idle Bloat & Leaks");
            AddFilterButton(filterStack, "governed", "Frozen / EcoQoS");
            AddFilterButton(filterStack, "protected", "Shielded / Capped");

            var thawBtn = new Button
            {
                Content = "❄ Thaw All",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                CornerRadius = new CornerRadius(8),
                Background = BrushHex("#3B82F6", 40),
                BorderBrush = BrushHex("#60A5FA"),
                Foreground = BrushHex("#93C5FD"),
                BorderThickness = new Thickness(1)
            };
            thawBtn.Click += (s, e) => ThawAllFrozenApps();
            filterStack.Children.Add(thawBtn);
            Grid.SetColumn(filterStack, 4);
            toolbar.Children.Add(filterStack);

            stack.Children.Add(toolbar);

            var scroll = new ScrollViewer { MaxHeight = 680, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _fullProcsPanel = new StackPanel { Spacing = 6, Padding = new Thickness(0, 0, 4, 8) };
            scroll.Content = _fullProcsPanel;
            stack.Children.Add(scroll);

            card.Child = stack;
            outer.Children.Add(card);
            return outer;
        }

        private void AddFilterButton(StackPanel parent, string key, string label)
        {
            bool active = key == _procFilter;
            var btn = new Button
            {
                Content = label,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                CornerRadius = new CornerRadius(8),
                Background = active ? BrushHex("#38BDF8", 45) : BrushHex("#151F35", 180),
                BorderBrush = active ? BrushHex("#38BDF8") : BrushHex("#94A3B8", 40),
                BorderThickness = new Thickness(1)
            };
            btn.Click += (s, e) =>
            {
                _procFilter = key;
                foreach (var kv in _filterButtons)
                {
                    bool on = kv.Key == _procFilter;
                    kv.Value.Background = on ? BrushHex("#38BDF8", 45) : BrushHex("#151F35", 180);
                    kv.Value.BorderBrush = on ? BrushHex("#38BDF8") : BrushHex("#94A3B8", 40);
                }
                RenderProcessLists(_engine.GetProcesses(_groupedProcesses));
            };
            _filterButtons[key] = btn;
            parent.Children.Add(btn);
        }

        private Grid BuildProfilesAndHotkeysView()
        {
            var outer = new Grid();
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Spacing = 12, ChildrenTransitions = CreateFluentTransitions() };

            // Profiles Card
            var profCard = CreateCardBorder();
            var profStack = new StackPanel { Spacing = 12 };

            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tStack = new StackPanel { Spacing = 2 };
            tStack.Children.Add(new TextBlock
            {
                Text = "PER-GAME & WORKLOAD AUTO-PROFILES",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#38BDF8")
            });
            tStack.Children.Add(new TextBlock
            {
                Text = "Automatically switches RAM thresholds, shields active game/render EXEs, and triggers launch sweeps",
                FontSize = 11.5,
                Foreground = BrushHex("#94A3B8")
            });
            hdr.Children.Add(tStack);

            var rightHdr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            _swProfileAuto = new ToggleSwitch
            {
                OnContent = "Auto-Detect Running EXEs",
                OffContent = "Manual Profile Only",
                IsOn = _engine.Config.ProfilesAutoSwitch
            };
            _swProfileAuto.Toggled += (s, e) =>
            {
                if (_isUpdatingUi) return;
                _engine.Config.ProfilesAutoSwitch = _swProfileAuto.IsOn;
                _engine.SaveConfig();
            };
            rightHdr.Children.Add(_swProfileAuto);

            var autoBtn = new Button
            {
                Content = "Return to Auto-Detect",
                FontSize = 11.5,
                CornerRadius = new CornerRadius(8)
            };
            autoBtn.Click += (s, e) =>
            {
                _engine.Config.ForcedProfileId = "";
                _engine.SaveConfig();
                RefreshAllUi();
                ShowToast("Profile Auto-Detection Active", "PulseRAM will automatically match running games and workloads.");
            };
            rightHdr.Children.Add(autoBtn);
            Grid.SetColumn(rightHdr, 1);
            hdr.Children.Add(rightHdr);
            profStack.Children.Add(hdr);

            _profilesGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12, ChildrenTransitions = CreateFluentTransitions() };
            _profilesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _profilesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _profilesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _profilesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            profStack.Children.Add(_profilesGrid);

            profCard.Child = profStack;
            stack.Children.Add(profCard);

            // Global Hotkeys Card
            var hkCard = CreateCardBorder();
            var hkStack = new StackPanel { Spacing = 10 };
            hkStack.Children.Add(new TextBlock
            {
                Text = "WIN32 SYSTEM-WIDE GLOBAL HOTKEYS",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#38BDF8")
            });

            var hkRow = new Grid { ColumnSpacing = 12 };
            hkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var hkBox1 = CreateCardBorder(new Thickness(12));
            var hk1Sp = new StackPanel { Spacing = 4 };
            hk1Sp.Children.Add(new TextBlock { Text = "Global Hotkey Listener", FontWeight = FontWeights.Bold, FontSize = 12.5 });
            hk1Sp.Children.Add(new TextBlock { Text = "Win32 RegisterHotKey active in background", FontSize = 11, Foreground = BrushHex("#94A3B8") });
            _swHotkeys = new ToggleSwitch { OnContent = "Hotkeys Enabled", OffContent = "Disabled", IsOn = _engine.Config.HotkeysEnabled };
            _swHotkeys.Toggled += (s, e) =>
            {
                if (_isUpdatingUi) return;
                _engine.Config.HotkeysEnabled = _swHotkeys.IsOn;
                _engine.SaveConfig();
                RegisterGlobalHotkeys();
            };
            hk1Sp.Children.Add(_swHotkeys);
            hkBox1.Child = hk1Sp;
            Grid.SetColumn(hkBox1, 0);
            hkRow.Children.Add(hkBox1);

            var hkBox2 = CreateCardBorder(new Thickness(12));
            var hk2Sp = new StackPanel { Spacing = 4 };
            hk2Sp.Children.Add(new TextBlock { Text = "Primary Quick-Clean Hotkey", FontWeight = FontWeights.Bold, FontSize = 12.5 });
            hk2Sp.Children.Add(new TextBlock { Text = "Ctrl + Shift + M", FontSize = 16, FontWeight = FontWeights.ExtraBold, Foreground = BrushHex("#38BDF8") });
            hk2Sp.Children.Add(new TextBlock { Text = "Executes instant Smart Trim anywhere in Windows", FontSize = 11, Foreground = BrushHex("#94A3B8") });
            hkBox2.Child = hk2Sp;
            Grid.SetColumn(hkBox2, 1);
            hkRow.Children.Add(hkBox2);

            var hkBox3 = CreateCardBorder(new Thickness(12));
            var hk3Sp = new StackPanel { Spacing = 4 };
            hk3Sp.Children.Add(new TextBlock { Text = "Secondary Deep-Purge Hotkey", FontWeight = FontWeights.Bold, FontSize = 12.5 });
            hk3Sp.Children.Add(new TextBlock { Text = "Ctrl + Shift + P", FontSize = 16, FontWeight = FontWeights.ExtraBold, Foreground = BrushHex("#A855F7") });
            hk3Sp.Children.Add(new TextBlock { Text = "Executes Deep Working Set + Standby Cache Purge", FontSize = 11, Foreground = BrushHex("#94A3B8") });
            hkBox3.Child = hk3Sp;
            Grid.SetColumn(hkBox3, 2);
            hkRow.Children.Add(hkBox3);

            hkStack.Children.Add(hkRow);
            hkCard.Child = hkStack;
            stack.Children.Add(hkCard);

            scroll.Content = stack;
            outer.Children.Add(scroll);
            return outer;
        }

        private Border BuildToastBanner()
        {
            var b = new Border
            {
                Background = BrushHex("#0D1526", 248),
                BorderBrush = BrushHex("#10B981", 150),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 11, 14, 11),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 16, 16),
                Visibility = Visibility.Collapsed,
                MaxWidth = 420,
                RenderTransform = new CompositeTransform()
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(8),
                Background = BrushHex("#10B981", 45),
                Child = new TextBlock
                {
                    Text = "⚡",
                    FontSize = 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            var textSp = new StackPanel { Spacing = 2 };
            _toastTitleText = new TextBlock { Text = "RAM Optimized", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = BrushHex("#F8FAFC") };
            _toastSubText = new TextBlock { Text = "", FontSize = 11, Foreground = BrushHex("#94A3B8"), TextWrapping = TextWrapping.Wrap };
            textSp.Children.Add(_toastTitleText);
            textSp.Children.Add(_toastSubText);
            row.Children.Add(textSp);
            b.Child = row;

            _toastTimer = DispatcherQueue.CreateTimer();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(4000);
            _toastTimer.Tick += (s, e) =>
            {
                _toastBanner.Visibility = Visibility.Collapsed;
                _toastTimer.Stop();
            };

            return b;
        }

        #endregion

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
                "smart" => "⚡ Smart Trim RAM Now",
                "deep" => "⚡ Run Deep Sweep Now",
                "standby" => "⚡ Purge Standby Cache Now",
                "max" => "⚡ Run Max Overdrive Now",
                _ => "⚡ Optimize RAM Now"
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

        private void ToggleMiniHudMode()
        {
            _isMiniHud = !_isMiniHud;
            SwitchView("overview");

            _rightColumnOverview.Visibility = _isMiniHud ? Visibility.Collapsed : Visibility.Visible;
            _bottomSplitOverview.Visibility = _isMiniHud ? Visibility.Collapsed : Visibility.Visible;
            _miniHudBtn.Content = _isMiniHud ? "Exit Mini HUD" : "Mini HUD";
            _miniHudBtn.BorderBrush = _isMiniHud ? BrushHex("#10B981") : BrushHex("#94A3B8", 45);

            try
            {
                if (_appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = _isMiniHud;
                }
                _appWindow.Resize(_isMiniHud ? new SizeInt32(430, 760) : new SizeInt32(1440, 930));
            }
            catch { }
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
            int count = _engine.ThawAllApps();
            RefreshAllUi();
            ShowToast("Thawed All Processes", $"Resumed {count} suspended application(s).");
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
            _optimizeBtnLabel.Text = "Optimizing Kernel Memory...";
            TriggerReactorShockwaveAnimation();

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var ev = _engine.OptimizeMemory(mode, "Manual", reason);
                DispatcherQueue.TryEnqueue(() =>
                {
                    _optimizeNowBtn.IsEnabled = true;
                    SelectCleaningMode(_selectedMode);
                    TriggerReactorShockwaveAnimation();
                    RefreshAllUi();
                    ShowToast(
                        $"Reclaimed {FormatMb(ev.FreedMb)} in {ev.DurationMs} ms!",
                        $"RAM dropped {ev.BeforePercent:F1}% → {ev.AfterPercent:F1}% ({ev.ProcessesTrimmed} processes trimmed)");
                });
            });
        }

        private void OnEngineOptimizationCompleted(OptimizationEvent ev)
        {
            if (ev.Trigger != "Manual")
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    TriggerReactorShockwaveAnimation();
                    RefreshAllUi();
                    ShowToast(
                        $"{ev.Trigger}: Freed {FormatMb(ev.FreedMb)}",
                        $"{ev.Reason} ({ev.BeforePercent:F1}% → {ev.AfterPercent:F1}%)");
                });
            }
        }

        private void ShowToast(string title, string sub)
        {
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

        private void RefreshAllUi()
        {
            if (_isUpdatingUi) return;
            _isUpdatingUi = true;
            try
            {
                var snap = _engine.GetSnapshot();
                var procs = _engine.GetProcesses(_groupedProcesses);
                var events = _engine.GetEvents();
                var telemetry = _engine.GetTelemetry();

                UpdateTrayTooltip(snap.Percent, snap.AvailableGb);

                // Top Command Header
                _clockText.Text = DateTime.Now.ToString("HH:mm:ss");
                _tabProcCountText.Text = procs.Count.ToString();
                _sessionFreedTopText.Text = FormatMb(snap.SessionFreedMb);

                _focusBoostBtn.Content = _engine.Config.FocusBoostActive ? "⚡ Focus Boost: ON (62%)" : "⚡ Focus Boost: OFF";
                _focusBoostBtn.BorderBrush = _engine.Config.FocusBoostActive ? BrushHex("#A855F7") : BrushHex("#94A3B8", 45);
                _focusBoostBtn.Background = _engine.Config.FocusBoostActive ? BrushHex("#A855F7", 45) : BrushHex("#151F35", 190);

                _apStatusDot.Fill = _engine.Config.AutoEnabled ? BrushHex("#10B981") : BrushHex("#64748B");
                _apStatusPillText.Text = _engine.Config.AutoEnabled
                    ? $"Auto-Pilot: {snap.EffectiveThresholdPercent}% · {snap.ActiveProfileName}"
                    : "Auto-Pilot: Paused";

                if (snap.IsAdmin)
                {
                    _adminBtn.Content = "✓ Kernel Admin Active";
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

                double cacheRatio = Math.Clamp(snap.StandbyAndCacheGb / Math.Max(0.1, snap.TotalGb), 0.0, 0.35);
                double usedRatio = Math.Clamp(snap.Percent / 100.0, 0.01, 0.999);
                double combinedRatio = Math.Clamp(usedRatio + cacheRatio, 0.01, 0.999);

                _targetUsedRatio = usedRatio;
                _targetCombinedRatio = combinedRatio;
                _targetThresholdPct = snap.EffectiveThresholdPercent;

                // Physical Memory Map
                _availSummaryText.Text = $"{snap.AvailableGb:F2} GB Available";
                double freeRatio = Math.Max(0.02, 1.0 - usedRatio - cacheRatio);
                _colSegUsed.Width = new GridLength(Math.Max(2, usedRatio * 100), GridUnitType.Star);
                _colSegCache.Width = new GridLength(Math.Max(2, cacheRatio * 100), GridUnitType.Star);
                _colSegFree.Width = new GridLength(Math.Max(2, freeRatio * 100), GridUnitType.Star);
                _legUsedText.Text = $"{snap.UsedGb:F2} GB";
                _legCacheText.Text = $"{snap.StandbyAndCacheGb:F2} GB";
                _legAvailText.Text = $"{snap.AvailableGb:F2} GB";

                double estReclaimMb = procs.Sum(p => p.ReclaimableMb);
                _optimizeBtnSub.Text = $"Est. Reclaimable: ~{FormatMb(estReclaimMb)}";

                // 6 Kernel Vitals
                _vitReclaimedVal.Text = FormatMb(snap.SessionFreedMb);
                _vitReclaimedSub.Text = $"{snap.OptimizationCount} sweeps completed";

                _vitCommitVal.Text = $"{snap.CommitTotalGb:F1} / {snap.CommitLimitGb:F1} GB";
                _vitCommitSub.Text = $"{snap.CommitPercent:F1}% virtual commit load";

                _vitPagefileVal.Text = snap.PagefileTotalGb > 0.05 ? $"{snap.PagefileUsedGb:F2} GB" : "0.00 GB";
                _vitPagefileSub.Text = snap.PagefileTotalGb > 0.05
                    ? $"{snap.PagefilePercent:F1}% of {snap.PagefileTotalGb:F1} GB swap"
                    : "Swap file idle";

                _vitKernelVal.Text = $"{snap.KernelTotalMb:F0} MB";
                _vitKernelSub.Text = $"Paged {snap.KernelPagedMb:F0}M · Non-Paged {snap.KernelNonPagedMb:F0}M";

                _vitCpuVal.Text = $"{snap.CpuPercent:F1}%";
                string fgShort = string.IsNullOrEmpty(snap.ForegroundApp) ? "Desktop" : snap.ForegroundApp;
                _vitCpuSub.Text = $"{snap.ProcessCount} Procs · FG: {fgShort}";

                int frozenCnt = procs.Count(p => p.IsFrozen);
                int ecoCnt = procs.Count(p => p.IsEco);
                int capCnt = _engine.Config.AppMemoryCapsMb?.Count ?? 0;
                int protCnt = _engine.Config.ProtectedApps?.Count ?? 0;
                _vitGovVal.Text = $"{protCnt} Shield · {capCnt} Cap";
                _vitGovSub.Text = $"{frozenCnt} Frozen · {ecoCnt} EcoQoS";

                // Scheduled Countdown
                if (_engine.Config.ScheduledTrimEnabled && snap.NextScheduledSeconds > 0)
                {
                    int mm = snap.NextScheduledSeconds / 60;
                    int ss = snap.NextScheduledSeconds % 60;
                    _lblSchedCountdown.Text = $"Next sweep in {mm:D2}:{ss:D2}";
                }
                else
                {
                    _lblSchedCountdown.Text = "Periodic timer off";
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
                    _lastSweepSummaryText.Text = $"Last Sweep [{latest.Timestamp}]: Reclaimed {FormatMb(latest.FreedMb)} ({latest.BeforePercent:F1}% → {latest.AfterPercent:F1}%)";
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
            foreach (var item in rawItems.OrderByDescending(p => p.MemoryMb).Take(8))
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
            foreach (var item in filtered.Take(50))
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
                var ev = _engine.OptimizeSingleApp(p.Name, "Manual App Trim");
                TriggerReactorShockwaveAnimation();
                RefreshAllUi();
                ShowToast($"Trimmed {p.Name}", $"Reclaimed {FormatMb(ev.FreedMb)} across {ev.ProcessesTrimmed} process(es).");
            });
            var btnEco = CreateMiniActionButton("Eco", p.IsEco, "#2DD4BF", () =>
            {
                var res = _engine.ToggleEcoApp(p.Name);
                RefreshAllUi();
                ShowToast(res.EcoActive ? $"EcoQoS Active: {p.Name}" : $"EcoQoS Off: {p.Name}", res.Message);
            });
            var btnFreeze = CreateMiniActionButton(p.IsFrozen ? "Thaw" : "Freeze", p.IsFrozen, "#60A5FA", () =>
            {
                var res = _engine.ToggleFreezeApp(p.Name);
                RefreshAllUi();
                ShowToast(res.Ok ? (res.Frozen ? $"Frozen {p.Name}" : $"Thawed {p.Name}") : "Protected Process", res.Message);
            });
            var btnCap = CreateMiniActionButton(p.CapMb.HasValue ? $"{p.CapMb.Value}M" : "Cap", p.CapMb.HasValue, "#38BDF8", () =>
            {
                ShowAppMemoryCapDialogAsync(p);
            });
            var btnShield = CreateMiniActionButton(p.IsProtected ? "Shielded" : "Shield", p.IsProtected, "#10B981", () =>
            {
                bool nowProt = _engine.ToggleProtectApp(p.Name);
                RefreshAllUi();
                ShowToast(nowProt ? $"Shielded {p.Name}" : $"Unshielded {p.Name}",
                    nowProt ? "Added to Auto-Pilot Shield whitelist." : "Removed from whitelist.");
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
                RequestedTheme = ElementTheme.Dark,
                XamlRoot = _rootGrid.XamlRoot
            };

            var sp = new StackPanel { Spacing = 10 };
            sp.Children.Add(new TextBlock
            {
                Text = $"Current Working Set: {FormatMb(proc.MemoryMb)}. When {proc.Name} is in the background and exceeds its memory ceiling, PulseRAM automatically trims it.",
                FontSize = 12,
                Foreground = BrushHex("#94A3B8"),
                TextWrapping = TextWrapping.Wrap
            });

            var inputBox = new TextBox
            {
                Header = "Memory Ceiling (MB)",
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
                    _engine.SetAppMemoryCap(proc.Name, capMb);
                    RefreshAllUi();
                    ShowToast($"Capped {proc.Name} at {capMb} MB", "Auto-Pilot will enforce this working-set ceiling.");
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                _engine.SetAppMemoryCap(proc.Name, null);
                RefreshAllUi();
                ShowToast($"Removed Cap on {proc.Name}", "Working-set ceiling cleared.");
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
                    Text = "No sweeps yet in this session. Click Optimize RAM Now or let Auto-Pilot govern memory automatically.",
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
                    Text = $"+{FormatMb(ev.FreedMb)} Reclaimed ({ev.Mode})",
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
                    Text = $"RAM {ev.BeforePercent:F1}% → {ev.AfterPercent:F1}% · {ev.ProcessesTrimmed} processes trimmed",
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
                sp.Children.Add(new TextBlock { Text = prof.Name, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = BrushHex("#F8FAFC") });
                sp.Children.Add(new TextBlock { Text = prof.Description, FontSize = 11.5, Foreground = BrushHex("#94A3B8"), TextWrapping = TextWrapping.Wrap });
                sp.Children.Add(new TextBlock
                {
                    Text = $"Trigger: {prof.RamThresholdPercent}% RAM · Mode: {prof.AutoMode.ToUpperInvariant()} · Auto-Shield: {(prof.AutoShieldMatched ? "ON" : "OFF")}",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushHex("#38BDF8")
                });

                var actBtn = new Button
                {
                    Content = isForced ? "✓ Active Profile (Click to Release)" : "Force Activate Profile",
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
