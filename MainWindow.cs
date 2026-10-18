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

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr pChangeFilterStruct);

        private const uint MSGFLT_ALLOW = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpBits);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

        private const int SM_CXSMICON = 49;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, uint ProcessInformationSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

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

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_INFO = 0x00000010;
        private const uint NIIF_INFO = 0x00000001;
        private const uint NIIF_WARNING = 0x00000002;
        private const uint NIN_BALLOONUSERCLICK = 0x0400 + 5;
        private const uint WM_GETMINMAXINFO = 0x0024;
        private const uint WM_ENDSESSION = 0x0016;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

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
        private IntPtr _trayPercentIcon = IntPtr.Zero;   // live RAM % icon currently shown (owned by us)
        private string _trayIconKey = "";                // percent|color|size of the icon being shown
        private bool _selfEfficiencyOn = false;

        // Theme: every colour goes through BrushHex, which swaps the dark palette for a light one.
        private static bool s_lightTheme;
        private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new Windows.UI.ViewManagement.UISettings();

        // Window placement
        private DispatcherQueueTimer? _placementTimer;

        // Commit-charge warning
        private Border _commitBanner = null!;
        private TextBlock _commitBannerTitle = null!;
        private TextBlock _commitBannerBody = null!;
        private Button _commitBannerFixBtn = null!;
        private CommitAdvice? _commitAdvice;

        private DateTime _lastErrorToast = DateTime.MinValue;
        private uint _showWindowMsg = 0;
        private uint _taskbarCreatedMsg = 0;
        private uint _exitForInstallerMsg = 0;
        private bool _animationsPaused = false;
        /// <summary>No ambient animation and a snapping gauge: lite mode, or Windows "Animation effects" is off.</summary>
        private bool _reduceMotion = false;
        private string _hotkeyPrimaryLabel = "";
        private string _hotkeySecondaryLabel = "";
        private string _hotkeyStatus = "";

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
        private Border _apStatusPill = null!;
        // Recreated on every UI build: a ToolTip may only belong to one element tree.
        private ToolTip _apStatusTip = new ToolTip();
        private ToolTip _adminTip = new ToolTip();
        private ToolTip _standbyTip = new ToolTip();
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
            Title = "PulseRAM";
            _engine = new NativeMemoryEngine();
            _engine.OnOptimizationCompleted += OnEngineOptimizationCompleted;

            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            s_lightTheme = ResolveLightTheme();
            ConfigureWindowFrameAndIcon();
            BuildWinUI3Interface();

            // Follow Windows when it switches between light and dark (event arrives on a background thread).
            _uiSettings.ColorValuesChanged += (s, e) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_engine.Config.Theme == "system") ApplyTheme();
            });

            _engine.OnCommitWarning += advice => DispatcherQueue.TryEnqueue(() => ShowCommitWarning(advice));

            // Remember size/position (debounced).
            _placementTimer = DispatcherQueue.CreateTimer();
            _placementTimer.Interval = TimeSpan.FromSeconds(1);
            _placementTimer.IsRepeating = false;
            _placementTimer.Tick += (s, e) => SaveWindowPlacement();
            _appWindow.Changed += (s, e) =>
            {
                if (e.DidPositionChange || e.DidSizeChange || e.DidPresenterChange)
                {
                    _placementTimer?.Stop();
                    _placementTimer?.Start();
                }
            };

            Closed += OnMainWindowClosed;

            // Initial UI Render (forced: the window is not visible yet)
            RefreshAllUi(force: true);

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

            ApplyPerformanceMode();
        }

        public void InitializeNativeHooksAfterActivate()
        {
            try
            {
                // Subclass HWND AFTER Activate() so WinUI 3's internal WindowProc is already chained
                _wndProcDelegate = CustomWndProc;
                _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

                // A second launch broadcasts this message so we can bring the existing window forward.
                // Allow it through UIPI in case this instance is elevated and the new one is not.
                _showWindowMsg = RegisterWindowMessage(Program.ShowWindowMessageName);
                if (_showWindowMsg != 0) ChangeWindowMessageFilterEx(_hwnd, _showWindowMsg, MSGFLT_ALLOW, IntPtr.Zero);
                // Explorer broadcasts TaskbarCreated after it restarts; the tray icon must be re-added then.
                _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
                if (_taskbarCreatedMsg != 0) ChangeWindowMessageFilterEx(_hwnd, _taskbarCreatedMsg, MSGFLT_ALLOW, IntPtr.Zero);
                // The (non-elevated) installer asks us to exit cleanly so frozen apps are thawed first;
                // taskkill alone cannot stop an elevated instance.
                _exitForInstallerMsg = RegisterWindowMessage(Program.ExitForInstallerMessageName);
                if (_exitForInstallerMsg != 0) ChangeWindowMessageFilterEx(_hwnd, _exitForInstallerMsg, MSGFLT_ALLOW, IntPtr.Zero);

                InitializeSystemTrayIcon();
                RegisterGlobalHotkeys();
                StartAmbientXamlAnimations();

                if (_launchArgs.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)))
                {
                    ShowWindow(_hwnd, 0);
                    RefreshAllUi(); // pauses animations while hidden
                }
                else
                {
                    ShowMainWindow();
                    // --compact: open straight into the compact widget (e.g. from a shortcut).
                    if (_launchArgs.Any(a => string.Equals(a, "--compact", StringComparison.OrdinalIgnoreCase)))
                        ToggleMiniHudMode();
                }

                if (!string.IsNullOrEmpty(_hotkeyStatus))
                    ShowToast("Hotkey unavailable", _hotkeyStatus);
                else if (Program.PreviousSessionCrashed)
                    ShowToast("PulseRAM closed unexpectedly last time", "Details are in the log: Profiles → Device → Logs.");
            }
            catch { }
        }

        private void ConfigureWindowFrameAndIcon()
        {
            try
            {
                _appWindow.Title = "PulseRAM";
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
                ApplyTitleBarColors();
                RestoreWindowPlacement();
            }
            catch { }
        }

        #region Theme, window placement

        private bool SystemUsesLightTheme()
        {
            try
            {
                var bg = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                return bg.R > 128;
            }
            catch
            {
                return false;
            }
        }

        private bool ResolveLightTheme() => _engine.Config.Theme switch
        {
            "light" => true,
            "dark" => false,
            _ => SystemUsesLightTheme()
        };

        private void ApplyTitleBarColors()
        {
            try
            {
                if (!AppWindowTitleBar.IsCustomizationSupported()) return;
                var tb = _appWindow.TitleBar;
                Color bg = s_lightTheme ? Color.FromArgb(255, 238, 242, 247) : Color.FromArgb(255, 6, 10, 18);
                Color fg = s_lightTheme ? Color.FromArgb(255, 15, 23, 42) : Color.FromArgb(255, 248, 250, 252);
                Color hover = s_lightTheme ? Color.FromArgb(255, 226, 232, 240) : Color.FromArgb(255, 30, 41, 59);
                tb.BackgroundColor = bg;
                tb.InactiveBackgroundColor = bg;
                tb.ForegroundColor = fg;
                tb.ButtonBackgroundColor = bg;
                tb.ButtonInactiveBackgroundColor = bg;
                tb.ButtonHoverBackgroundColor = hover;
                tb.ButtonForegroundColor = fg;
                tb.ButtonHoverForegroundColor = fg;
            }
            catch { }
        }

        /// <summary>Switches light/dark by rebuilding the (code-built) interface with the other palette.</summary>
        private void ApplyTheme()
        {
            bool light = ResolveLightTheme();
            if (light == s_lightTheme) return;
            s_lightTheme = light;
            ApplyTitleBarColors();
            try
            {
                _toastTimer?.Stop();
                foreach (var sb in new[] { _reactorBreathingStoryboard, _reactorOrbitalStoryboard, _logoPulseStoryboard })
                {
                    try { sb?.Stop(); } catch { }
                }
                _reactorBreathingStoryboard = null;
                _reactorOrbitalStoryboard = null;
                _logoPulseStoryboard = null;
                _modeCards.Clear();
                _filterButtons.Clear();

                BuildWinUI3Interface();

                _displayedPercent = 0;
                _displayedUsedRatio = 0.01;
                _displayedCombinedRatio = 0.01;
                if (_isMiniHud) ApplyMiniHudLayout(); // stay in the Mini HUD; window size and on-top are untouched
                else SwitchView(_currentView);
                SelectCleaningMode(_selectedMode);
                ApplyPerformanceMode();
                StartAmbientXamlAnimations();
                RefreshAllUi(force: true);
                if (_commitAdvice != null) ShowCommitWarning(_commitAdvice, announce: false);
                Log.Info($"Theme switched to {(light ? "light" : "dark")}");
            }
            catch (Exception ex)
            {
                Log.Error("Theme switch failed", ex);
            }
        }

        /// <summary>
        /// Restores the last size/position if it is still on a connected screen; otherwise centres a size that
        /// fits the primary work area (1440×930 at most, so small laptop screens aren't overflowed).
        /// </summary>
        private void RestoreWindowPlacement()
        {
            try
            {
                var cfg = _engine.Config;
                if (cfg.WindowX is int x && cfg.WindowY is int y && cfg.WindowWidth is int w && cfg.WindowHeight is int h && w >= 300 && h >= 300)
                {
                    var area = DisplayArea.GetFromPoint(new PointInt32(x + Math.Min(w, 200) / 2, y + 16), DisplayAreaFallback.None);
                    if (area != null)
                    {
                        var wa = area.WorkArea;
                        // Move first, then size: a single MoveAndResize onto a monitor with a different DPI can be
                        // rescaled by the DPI-change handling and come back the wrong size.
                        _appWindow.Move(new PointInt32(x, y));
                        _appWindow.Resize(new SizeInt32(Math.Min(w, wa.Width), Math.Min(h, wa.Height)));
                        return;
                    }
                }

                var primary = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
                var work = primary?.WorkArea ?? new RectInt32(0, 0, 1440, 930);
                int width = Math.Min(1440, (int)(work.Width * 0.95));
                int height = Math.Min(930, (int)(work.Height * 0.95));
                _appWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not restore window placement", ex);
            }
        }

        private void SaveWindowPlacement()
        {
            try
            {
                if (!IsWindowVisible(_hwnd) || IsIconic(_hwnd)) return;
                var cfg = _engine.Config;
                if (_isMiniHud)
                {
                    var mp = _appWindow.Position;
                    if (cfg.MiniHudX != mp.X || cfg.MiniHudY != mp.Y)
                    {
                        cfg.MiniHudX = mp.X;
                        cfg.MiniHudY = mp.Y;
                        _engine.SaveConfig();
                    }
                    return;
                }
                bool maximized = _appWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Maximized;
                bool changed = maximized != cfg.WindowMaximized;
                cfg.WindowMaximized = maximized;
                if (!maximized)
                {
                    var pos = _appWindow.Position;
                    var size = _appWindow.Size;
                    changed |= cfg.WindowX != pos.X || cfg.WindowY != pos.Y || cfg.WindowWidth != size.Width || cfg.WindowHeight != size.Height;
                    cfg.WindowX = pos.X;
                    cfg.WindowY = pos.Y;
                    cfg.WindowWidth = size.Width;
                    cfg.WindowHeight = size.Height;
                }
                if (changed) _engine.SaveConfig();
            }
            catch { }
        }

        #endregion

        #region Commit warning, error reporting

        private Border BuildCommitBanner()
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 10, 10),
                Visibility = Visibility.Collapsed
            };
            var g = new Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon { Glyph = "\uE7BA", FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(icon);

            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            _commitBannerTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = BrushHex("#F8FAFC") };
            _commitBannerBody = new TextBlock { FontSize = 12, Foreground = BrushHex("#94A3B8"), TextWrapping = TextWrapping.Wrap };
            text.Children.Add(_commitBannerTitle);
            text.Children.Add(_commitBannerBody);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            _commitBannerFixBtn = new Button { Content = "Page file settings", FontSize = 12, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(_commitBannerFixBtn, "Opens Performance Options → Advanced → Virtual memory.");
            _commitBannerFixBtn.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("SystemPropertiesPerformance.exe") { UseShellExecute = true }); }
                catch (Exception ex) { Log.Warn("Could not open page-file settings", ex); }
            };
            Grid.SetColumn(_commitBannerFixBtn, 2);
            g.Children.Add(_commitBannerFixBtn);

            var dismiss = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }, Padding = new Thickness(8), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(dismiss, "Dismiss");
            dismiss.Click += (s, e) => { _commitBanner.Visibility = Visibility.Collapsed; _commitAdvice = null; };
            Grid.SetColumn(dismiss, 3);
            g.Children.Add(dismiss);

            b.Child = g;
            b.Tag = icon;
            return b;
        }

        /// <summary>Shows the commit-charge warning as a banner, and as a tray notification while hidden.</summary>
        private void ShowCommitWarning(CommitAdvice advice, bool announce = true)
        {
            _commitAdvice = advice;
            bool critical = advice.Level == "critical";
            string hex = critical ? "#F43F5E" : "#F59E0B";
            _commitBanner.Background = BrushHex(hex, 30);
            _commitBanner.BorderBrush = BrushHex(hex, 140);
            if (_commitBanner.Tag is FontIcon icon) icon.Foreground = BrushHex(hex);
            _commitBannerTitle.Text = advice.Title;
            _commitBannerBody.Text = advice.Body;
            _commitBannerFixBtn.Visibility = advice.SuggestPageFile ? Visibility.Visible : Visibility.Collapsed;
            _commitBanner.Visibility = Visibility.Visible;

            if (announce && !advice.IsRepeat && !IsDashboardVisible())
                ShowTrayBalloon($"PulseRAM: {advice.Title}", advice.Body, warning: true);
        }

        private void ShowTrayBalloon(string title, string text, bool warning)
        {
            if (!_trayInitialized) return;
            try
            {
                _trayData.uFlags = NIF_INFO;
                _trayData.szInfoTitle = title.Length > 63 ? title.Substring(0, 63) : title;
                _trayData.szInfo = text.Length > 255 ? text.Substring(0, 252) + "…" : text;
                _trayData.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
                Shell_NotifyIcon(NIM_MODIFY, ref _trayData);
            }
            catch { }
            finally
            {
                _trayData.szInfo = "";
                _trayData.szInfoTitle = "";
            }
        }

        /// <summary>Called (any thread) when a UI exception was caught and logged.</summary>
        public void ReportUnexpectedError(string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if ((DateTime.Now - _lastErrorToast).TotalSeconds < 60) return;
                _lastErrorToast = DateTime.Now;
                try { ShowToast("Something went wrong", "PulseRAM kept running. Details: Profiles → Device → Logs."); } catch { }
            });
        }

        #endregion

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
            // Ambient loops are skipped entirely in reduce-motion / lite mode (re-started if the user picks Full).
            if (_reduceMotion || _reactorBreathingStoryboard != null) return;
            StartAmbientXamlAnimationsCore();
            // Keep the storyboards consistent with the current paused state (e.g. launched hidden with --tray).
            if (_animationsPaused)
            {
                foreach (var sb in new[] { _reactorBreathingStoryboard, _reactorOrbitalStoryboard, _logoPulseStoryboard })
                {
                    try { sb?.Pause(); } catch { }
                }
            }
        }

        private void StartAmbientXamlAnimationsCore()
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
            if (_reduceMotion) return;
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

        private void OnGaugeAnimationFrame(DispatcherQueueTimer sender, object args) => StepGauge(0.16);

        /// <summary>Moves the gauge toward its targets; factor 1.0 jumps straight there (reduce-motion).</summary>
        private void StepGauge(double lerp)
        {
            try
            {
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
                    szTip = "PulseRAM",
                    szInfo = "",
                    szInfoTitle = ""
                };
                _trayInitialized = Shell_NotifyIcon(NIM_ADD, ref _trayData);
            }
            catch { }
        }

        private void UpdateTrayTooltip(MemorySnapshot snap)
        {
            if (!_trayInitialized) return;
            try
            {
                _trayData.uFlags = NIF_TIP;
                _trayData.szTip = $"PulseRAM: {snap.Percent:F1}% used · {snap.AvailableGb:F2} GB available";

                if (_engine.Config.TrayShowsPercent)
                {
                    int pct = (int)Math.Clamp(Math.Round(snap.Percent), 0, 99);
                    string hex = snap.PressureLevel == "CRITICAL" ? "#E11D48" : (snap.PressureLevel == "ELEVATED" ? "#F59E0B" : "#059669");
                    int size = TrayIconSize();
                    string key = $"{pct}|{hex}|{size}";
                    if (key != _trayIconKey)
                    {
                        IntPtr icon = CreatePercentIcon(pct, hex, size, darkText: snap.PressureLevel == "ELEVATED");
                        if (icon != IntPtr.Zero)
                        {
                            _trayData.uFlags |= NIF_ICON;
                            _trayData.hIcon = icon;
                            if (Shell_NotifyIcon(NIM_MODIFY, ref _trayData))
                            {
                                if (_trayPercentIcon != IntPtr.Zero) DestroyIcon(_trayPercentIcon);
                                _trayPercentIcon = icon;
                                _trayIconKey = key;
                            }
                            else
                            {
                                DestroyIcon(icon);
                            }
                            return;
                        }
                    }
                }
                else if (_trayPercentIcon != IntPtr.Zero)
                {
                    // Switched off: go back to the app icon.
                    _trayData.uFlags |= NIF_ICON;
                    _trayData.hIcon = _customHIcon != IntPtr.Zero ? _customHIcon : LoadIcon(IntPtr.Zero, new IntPtr(32512));
                    Shell_NotifyIcon(NIM_MODIFY, ref _trayData);
                    DestroyIcon(_trayPercentIcon);
                    _trayPercentIcon = IntPtr.Zero;
                    _trayIconKey = "";
                    return;
                }

                Shell_NotifyIcon(NIM_MODIFY, ref _trayData);
            }
            catch { }
        }

        /// <summary>Small-icon size for the tray at this window's DPI (16 px at 100%, 24 at 150%, 32 at 200%).</summary>
        private int TrayIconSize()
        {
            try
            {
                uint dpi = GetDpiForWindow(_hwnd);
                int size = GetSystemMetricsForDpi(SM_CXSMICON, dpi == 0 ? 96 : dpi);
                return Math.Clamp(size, 16, 64);
            }
            catch
            {
                return 16;
            }
        }

        // 3×5 pixel digits, one string per row ('#' = lit).
        private static readonly string[][] PixelDigits =
        {
            new[] { "###", "#.#", "#.#", "#.#", "###" }, // 0
            new[] { ".#.", "##.", ".#.", ".#.", "###" }, // 1
            new[] { "###", "..#", "###", "#..", "###" }, // 2
            new[] { "###", "..#", "###", "..#", "###" }, // 3
            new[] { "#.#", "#.#", "###", "..#", "..#" }, // 4
            new[] { "###", "#..", "###", "..#", "###" }, // 5
            new[] { "###", "#..", "###", "#.#", "###" }, // 6
            new[] { "###", "..#", "..#", "..#", "..#" }, // 7
            new[] { "###", "#.#", "###", "#.#", "###" }, // 8
            new[] { "###", "#.#", "###", "..#", "###" }, // 9
        };

        /// <summary>
        /// Draws the RAM percentage as a crisp pixel-font badge (rounded square colored by memory pressure) and
        /// returns an HICON the caller must destroy. Pure GDI, no extra libraries, scales with DPI.
        /// </summary>
        private static IntPtr CreatePercentIcon(int percent, string bgHex, int size, bool darkText)
        {
            string h = bgHex.TrimStart('#');
            uint bg = 0xFF000000u | (Convert.ToUInt32(h.Substring(0, 2), 16) << 16) | (Convert.ToUInt32(h.Substring(2, 2), 16) << 8) | Convert.ToUInt32(h.Substring(4, 2), 16);
            uint fg = darkText ? 0xFF111827u : 0xFFFFFFFFu;

            var px = new uint[size * size];
            double r = Math.Max(2, size / 5);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance from the nearest corner-circle centre; only matters inside the corner squares.
                    double fx = x + 0.5, fy = y + 0.5;
                    double qx = fx < r ? r - fx : (fx > size - r ? fx - (size - r) : 0);
                    double qy = fy < r ? r - fy : (fy > size - r ? fy - (size - r) : 0);
                    if (qx * qx + qy * qy <= r * r) px[y * size + x] = bg;
                }
            }

            string text = percent.ToString();
            int scale = Math.Max(1, Math.Min((size - 2) / (text.Length * 4 - 1), (size - 2) / 5));
            int textW = (text.Length * 4 - 1) * scale;
            int textH = 5 * scale;
            int ox = (size - textW) / 2;
            int oy = (size - textH) / 2;
            for (int c = 0; c < text.Length; c++)
            {
                var glyph = PixelDigits[text[c] - '0'];
                for (int gy = 0; gy < 5; gy++)
                {
                    for (int gx = 0; gx < 3; gx++)
                    {
                        if (glyph[gy][gx] != '#') continue;
                        for (int sy = 0; sy < scale; sy++)
                        {
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int x = ox + (c * 4 + gx) * scale + sx;
                                int y = oy + gy * scale + sy;
                                if (x >= 0 && x < size && y >= 0 && y < size) px[y * size + x] = fg;
                            }
                        }
                    }
                }
            }

            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };
            IntPtr color = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (color == IntPtr.Zero) return IntPtr.Zero;
            IntPtr mask = IntPtr.Zero;
            try
            {
                var bytes = new int[px.Length];
                Buffer.BlockCopy(px, 0, bytes, 0, px.Length * 4);
                Marshal.Copy(bytes, 0, bits, bytes.Length);
                // Monochrome AND-mask, all zero: the colour bitmap's alpha channel carries transparency.
                int maskStride = ((size + 15) / 16) * 2;
                IntPtr maskBits = Marshal.AllocHGlobal(maskStride * size);
                try
                {
                    Marshal.Copy(new byte[maskStride * size], 0, maskBits, maskStride * size);
                    mask = CreateBitmap(size, size, 1, 1, maskBits);
                }
                finally
                {
                    Marshal.FreeHGlobal(maskBits);
                }
                var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
                return CreateIconIndirect(ref info);
            }
            finally
            {
                DeleteObject(color);
                if (mask != IntPtr.Zero) DeleteObject(mask);
            }
        }

        /// <summary>
        /// While the dashboard is hidden PulseRAM only watches memory, so let Windows run it in Efficiency Mode
        /// (EcoQoS + below-normal priority) — the same trick H.NotifyIcon uses for tray apps.
        /// </summary>
        private void SetSelfEfficiencyMode(bool on)
        {
            if (on == _selfEfficiencyOn) return;
            try
            {
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = on ? 1u : 0u,   // 0 = let Windows decide again
                    StateMask = on ? 1u : 0u
                };
                bool qos = SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                bool prio = SetPriorityClass(GetCurrentProcess(), on ? 0x00004000u /* BELOW_NORMAL */ : 0x00000020u /* NORMAL */);
                if (qos || prio) _selfEfficiencyOn = on;
            }
            catch { }
        }

        /// <summary>
        /// Parses "Ctrl+Alt+Shift+M" style strings (modifiers: Ctrl/Control, Alt, Shift, Win; key: A–Z, 0–9, F1–F24).
        /// </summary>
        private static bool TryParseHotkey(string? text, out uint modifiers, out uint vk, out string label)
        {
            modifiers = 0;
            vk = 0;
            label = "";
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return false;

            var labels = new List<string>();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                    case "alt": modifiers |= MOD_ALT; break;
                    case "shift": modifiers |= MOD_SHIFT; break;
                    case "win": case "windows": modifiers |= MOD_WIN; break;
                    default: return false;
                }
            }
            if (modifiers == 0) return false;
            if ((modifiers & MOD_CONTROL) != 0) labels.Add("Ctrl");
            if ((modifiers & MOD_ALT) != 0) labels.Add("Alt");
            if ((modifiers & MOD_SHIFT) != 0) labels.Add("Shift");
            if ((modifiers & MOD_WIN) != 0) labels.Add("Win");

            string key = parts[^1].ToUpperInvariant();
            if (key.Length == 1 && ((key[0] >= 'A' && key[0] <= 'Z') || (key[0] >= '0' && key[0] <= '9')))
            {
                vk = key[0];
            }
            else if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.Substring(1), out int fn) && fn >= 1 && fn <= 24)
            {
                vk = (uint)(0x70 + fn - 1); // VK_F1..VK_F24
            }
            else
            {
                return false;
            }
            labels.Add(key);
            label = string.Join(" + ", labels);
            return true;
        }

        private void RegisterGlobalHotkeys()
        {
            _hotkeyStatus = "";
            bool p1 = TryParseHotkey(_engine.Config.HotkeyPrimary, out uint m1, out uint vk1, out _hotkeyPrimaryLabel);
            bool p2 = TryParseHotkey(_engine.Config.HotkeySecondary, out uint m2, out uint vk2, out _hotkeySecondaryLabel);
            if (!p1) _hotkeyPrimaryLabel = $"Invalid: {_engine.Config.HotkeyPrimary}";
            if (!p2) _hotkeySecondaryLabel = $"Invalid: {_engine.Config.HotkeySecondary}";

            try
            {
                UnregisterHotKey(_hwnd, 9001);
                UnregisterHotKey(_hwnd, 9002);
                if (!_engine.Config.HotkeysEnabled) return;

                var problems = new List<string>();
                if (!p1) problems.Add($"\"{_engine.Config.HotkeyPrimary}\" is not a valid hotkey");
                else if (!RegisterHotKey(_hwnd, 9001, m1 | MOD_NOREPEAT, vk1)) problems.Add($"{_hotkeyPrimaryLabel} is already used by another app");
                if (!p2) problems.Add($"\"{_engine.Config.HotkeySecondary}\" is not a valid hotkey");
                else if (!RegisterHotKey(_hwnd, 9002, m2 | MOD_NOREPEAT, vk2)) problems.Add($"{_hotkeySecondaryLabel} is already used by another app");

                if (problems.Count > 0)
                    _hotkeyStatus = string.Join("; ", problems) +
                        $". Change HotkeyPrimary / HotkeySecondary in {System.IO.Path.Combine(NativeMemoryEngine.DataDirectory, "pulseram_config.json")}.";
            }
            catch { }
        }

        /// <summary>Shows, restores and focuses the dashboard, then renders it immediately.</summary>
        public void ShowMainWindow()
        {
            // SW_SHOWMAXIMIZED if it was maximised last time, otherwise SW_RESTORE.
            ShowWindow(_hwnd, _engine.Config.WindowMaximized && !_isMiniHud ? 3 : 9);
            SetForegroundWindow(_hwnd);
            RefreshAllUi(force: true);
        }

        private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_ENDSESSION && wParam != IntPtr.Zero)
            {
                // Windows is restarting or signing out: the process is about to end without Window.Closed,
                // so thaw frozen apps, save state and mark the session as cleanly ended now.
                ShutdownCleanup("Windows session ending");
            }
            if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
            {
                // Minimum size so the layout never collapses (smaller for the Mini HUD).
                IntPtr result = CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
                try
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    double scale = Math.Max(1.0, GetDpiForWindow(hWnd) / 96.0);
                    int minW = (int)((_isMiniHud ? 240 : 900) * scale);
                    int minH = (int)((_isMiniHud ? 80 : 600) * scale);
                    // Never demand more than the screen has (e.g. 1366×768 at 125%).
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(MonitorFromWindow(hWnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref mi))
                    {
                        minW = Math.Min(minW, mi.rcWork.Right - mi.rcWork.Left);
                        minH = Math.Min(minH, mi.rcWork.Bottom - mi.rcWork.Top);
                    }
                    mmi.ptMinTrackSize.X = minW;
                    mmi.ptMinTrackSize.Y = minH;
                    Marshal.StructureToPtr(mmi, lParam, false);
                }
                catch { }
                return result;
            }
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == 9001)
                {
                    DispatcherQueue.TryEnqueue(() => RunOptimizationAsync("smart", $"Global Hotkey ({_hotkeyPrimaryLabel})"));
                }
                else if (id == 9002)
                {
                    DispatcherQueue.TryEnqueue(() => RunOptimizationAsync("deep", $"Global Hotkey ({_hotkeySecondaryLabel})"));
                }
            }
            else if (msg == WM_USER_TRAY)
            {
                uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
                if (mouseMsg == WM_LBUTTONDBLCLK || mouseMsg == NIN_BALLOONUSERCLICK)
                {
                    ShowMainWindow();
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    ShowTrayContextMenu();
                }
            }
            else if (_showWindowMsg != 0 && msg == _showWindowMsg)
            {
                DispatcherQueue.TryEnqueue(ShowMainWindow);
                return IntPtr.Zero;
            }
            else if (_exitForInstallerMsg != 0 && msg == _exitForInstallerMsg)
            {
                DispatcherQueue.TryEnqueue(Close);
                return IntPtr.Zero;
            }
            else if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
            {
                // Explorer restarted: the old icon is gone, add it again.
                _trayInitialized = false;
                _trayIconKey = "";
                InitializeSystemTrayIcon();
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
                string hk1 = _engine.Config.HotkeysEnabled ? $" ({_hotkeyPrimaryLabel})" : "";
                string hk2 = _engine.Config.HotkeysEnabled ? $" ({_hotkeySecondaryLabel})" : "";
                AppendMenu(hMenu, MF_STRING, new UIntPtr(2), $"Smart Trim Idle Apps Now{hk1}");
                AppendMenu(hMenu, MF_STRING, new UIntPtr(3), $"Deep Sweep Now{hk2}");
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
                        ShowMainWindow();
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

        private void OnMainWindowClosed(object sender, WindowEventArgs args) => ShutdownCleanup("Clean shutdown");

        private bool _shutdownDone;

        /// <summary>Idempotent: runs on window close and on WM_ENDSESSION (Windows restart / sign-out).</summary>
        private void ShutdownCleanup(string reason)
        {
            if (_shutdownDone) return;
            _shutdownDone = true;
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
                if (_trayPercentIcon != IntPtr.Zero)
                {
                    DestroyIcon(_trayPercentIcon);
                    _trayPercentIcon = IntPtr.Zero;
                }
                SaveWindowPlacement();
                _engine.Dispose();
                Log.Info(reason);
                Log.MarkSessionEnd();
            }
            catch (Exception ex)
            {
                Log.Error("Shutdown failed", ex);
            }
        }

        #endregion

        #region WinUI 3 Visual Tree Construction

        /// <summary>Dark-palette colour → light-theme equivalent (backgrounds lighten, text and accents darken for contrast).</summary>
        private static readonly Dictionary<string, string> LightPalette = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // surfaces
            ["#060A12"] = "#EEF2F7", ["#0D1424"] = "#FFFFFF", ["#151F35"] = "#E2E8F0", ["#0D1526"] = "#FFFFFF",
            ["#0A1326"] = "#F8FAFC", ["#091122"] = "#F1F5F9", ["#1E293B"] = "#E2E8F0",
            // text
            ["#F8FAFC"] = "#0F172A", ["#94A3B8"] = "#475569",
            // accents (darker shades keep contrast on light backgrounds)
            ["#38BDF8"] = "#0284C7", ["#10B981"] = "#059669", ["#34D399"] = "#059669", ["#F59E0B"] = "#B45309",
            ["#FBBF24"] = "#B45309", ["#F43F5E"] = "#E11D48", ["#A855F7"] = "#9333EA", ["#818CF8"] = "#6366F1",
            ["#60A5FA"] = "#2563EB", ["#93C5FD"] = "#3B82F6", ["#2DD4BF"] = "#0D9488"
        };

        private static SolidColorBrush BrushHex(string hex, byte alpha = 255)
        {
            if (s_lightTheme && LightPalette.TryGetValue(hex, out var lightHex)) hex = lightHex;
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
            _rootGrid = new Grid { Background = BrushHex("#060A12", 245) };
            _rootGrid.RequestedTheme = s_lightTheme ? ElementTheme.Light : ElementTheme.Dark;

            _fullLayout = new Grid
            {
                Padding = new Thickness(16, 12, 16, 14),
                RowSpacing = 12
            };
            _fullLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _fullLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _fullLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _rootGrid.Children.Add(_fullLayout);
            _miniHudPanel = BuildMiniHudPanel();
            _rootGrid.Children.Add(_miniHudPanel);

            // 1. Top Command Header
            var topBar = BuildTopCommandBar();
            Grid.SetRow(topBar, 0);
            _fullLayout.Children.Add(topBar);

            // 1b. Commit-charge warning (hidden until needed)
            _commitBanner = BuildCommitBanner();
            Grid.SetRow(_commitBanner, 1);
            _fullLayout.Children.Add(_commitBanner);

            // 2. View Host Container
            var viewHost = new Grid();
            Grid.SetRow(viewHost, 2);

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

            _fullLayout.Children.Add(viewHost);
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
            titleStack.Children.Add(titleLine);
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

            _tabBtnOverview = CreateNavTabButton("Overview", true, () => SwitchView("overview"));

            var procTabContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            procTabContent.Children.Add(new TextBlock { Text = "Processes", FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
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

            _tabBtnProfiles = CreateNavTabButton("Profiles", false, () => SwitchView("profiles"));

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
                Content = "⚡ Focus",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(12, 5, 12, 5),
                Background = BrushHex("#151F35", 190),
                BorderBrush = BrushHex("#94A3B8", 45),
                BorderThickness = new Thickness(1)
            };
            _focusBoostBtn.Click += (s, e) => ToggleFocusBoost();
            ToolTipService.SetToolTip(_focusBoostBtn, "Focus Boost: temporarily lowers the Auto-Pilot trigger to 62% and trims idle apps now.");
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
            _apStatusPillText = new TextBlock { Text = "Auto 80%", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#F8FAFC") };
            apRow.Children.Add(_apStatusDot);
            apRow.Children.Add(_apStatusPillText);
            apPill.Child = apRow;
            _apStatusPill = apPill;
            _apStatusTip = new ToolTip();
            ToolTipService.SetToolTip(_apStatusPill, _apStatusTip);
            rightStack.Children.Add(apPill);

            _sessionFreedTopText = new TextBlock(); // shown on the TRIMMED card instead

            // Mini HUD Toggle
            _miniHudBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE944", FontSize = 13 },
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(11, 5, 11, 5),
                Background = BrushHex("#151F35", 190),
                BorderBrush = BrushHex("#94A3B8", 45),
                BorderThickness = new Thickness(1)
            };
            _miniHudBtn.Click += (s, e) => ToggleMiniHudMode();
            ToolTipService.SetToolTip(_miniHudBtn, "Compact, always-on-top view");
            rightStack.Children.Add(_miniHudBtn);

            // Admin Button
            _adminBtn = new Button
            {
                Content = "Admin",
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
            _adminTip = new ToolTip { Content = "Restart as administrator to enable standby-cache purges and page combining." };
            ToolTipService.SetToolTip(_adminBtn, _adminTip);
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
                Text = "MEMORY",
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
            mapHdr.Children.Add(new TextBlock { Text = "RAM", FontSize = 11, Foreground = BrushHex("#94A3B8") });
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
            var standbyCell = CreateLegendCell("Standby", "#818CF8", out _legCacheText, 1);
            _standbyTip = new ToolTip();
            ToolTipService.SetToolTip(standbyCell, _standbyTip);
            legGrid.Children.Add(standbyCell);
            legGrid.Children.Add(CreateLegendCell("Available", "#10B981", out _legAvailText, 2));
            mapStack.Children.Add(legGrid);
            mapBox.Child = mapStack;
            stack.Children.Add(mapBox);

            // 4 Cleaning Mode Selector Cards (2x2 Grid)
            stack.Children.Add(new TextBlock
            {
                Text = "MODE",
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#94A3B8")
            });

            var modeGrid = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
            modeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            modeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddModeCard(modeGrid, "smart", "Smart", "Safe", "Trims idle background apps only. Never touches the active window or shielded apps.", 0, 0);
            AddModeCard(modeGrid, "deep", "Deep", "", "Trims every unshielded app. As administrator, also purges the low-priority standby cache and combines identical pages.", 0, 1);
            AddModeCard(modeGrid, "standby", "Standby", "Admin", "Empties the Windows standby cache and flushes modified pages. Requires administrator.", 1, 0);
            AddModeCard(modeGrid, "max", "Max", "Admin", "Deep trim + page combining + full standby purge. The kernel part requires administrator.", 1, 1);
            stack.Children.Add(modeGrid);

            // Primary Optimize Now Button
            var ctaStack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            _optimizeBtnLabel = new TextBlock
            {
                Text = "⚡ Clean · Smart",
                FontSize = 14.5,
                FontWeight = FontWeights.ExtraBold,
                Foreground = BrushHex("#FFFFFF"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _optimizeBtnSub = new TextBlock
            {
                Text = "--",
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
                Text = "Ready",
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
            border.Child = sp;
            ToolTipService.SetToolTip(border, desc);

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
                Text = "LAST 60 S",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#38BDF8")
            });
            _chartMetaText = new TextBlock
            {
                Text = "RAM · CPU · trigger",
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

            vitalsGrid.Children.Add(CreateVitalCard("TRIMMED", "#34D399", out _vitReclaimedVal, out _vitReclaimedSub, 0, 0));
            vitalsGrid.Children.Add(CreateVitalCard("COMMIT", "#F8FAFC", out _vitCommitVal, out _vitCommitSub, 0, 1));
            vitalsGrid.Children.Add(CreateVitalCard("PAGEFILE", "#F8FAFC", out _vitPagefileVal, out _vitPagefileSub, 0, 2));
            vitalsGrid.Children.Add(CreateVitalCard("KERNEL", "#F8FAFC", out _vitKernelVal, out _vitKernelSub, 1, 0));
            vitalsGrid.Children.Add(CreateVitalCard("CPU", "#38BDF8", out _vitCpuVal, out _vitCpuSub, 1, 1));
            vitalsGrid.Children.Add(CreateVitalCard("RULES", "#A855F7", out _vitGovVal, out _vitGovSub, 1, 2));

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
                Text = "AUTO-PILOT",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#38BDF8")
            });
            hdr.Children.Add(titleStack);

            _swAutoPilot = new ToggleSwitch
            {
                OnContent = "On",
                OffContent = "Off",
                IsOn = _engine.Config.AutoEnabled
            };
            _swAutoPilot.Toggled += (s, e) =>
            {
                if (_isUpdatingUi) return;
                _engine.Config.AutoEnabled = _swAutoPilot.IsOn;
                _engine.SaveConfig();
                RefreshAllUi();
            };
            ToolTipService.SetToolTip(_swAutoPilot, "Automatically trims idle apps when RAM crosses the trigger or free RAM drops below the floor.");
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
            s1Top.Children.Add(new TextBlock { Text = "Trigger at", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#F8FAFC") });
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
            s2Top.Children.Add(new TextBlock { Text = "Min free", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#F8FAFC") });
            _lblMinFreeVal = new TextBlock { Text = "1.0 GB", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = BrushHex("#38BDF8") };
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
            presetStack.Children.Add(CreateQuickPresetBtn("Gaming", 70, 3.0, "smart"));
            presetStack.Children.Add(CreateQuickPresetBtn("Balanced", 80, 2.0, "smart"));
            presetStack.Children.Add(CreateQuickPresetBtn("Heavy", 88, 1.5, "deep"));
            Grid.SetColumn(presetStack, 0);
            optsGrid.Children.Add(presetStack);

            _cmbAutoMode = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            _cmbAutoMode.Items.Add(new ComboBoxItem { Content = "Smart", Tag = "smart" });
            _cmbAutoMode.Items.Add(new ComboBoxItem { Content = "Deep", Tag = "deep" });
            _cmbAutoMode.Items.Add(new ComboBoxItem { Content = "Standby", Tag = "standby" });
            _cmbAutoMode.Items.Add(new ComboBoxItem { Content = "Max", Tag = "max" });
            _cmbAutoMode.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "smart", "deep", "standby", "max" }, _engine.Config.AutoMode));
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
            int[] cooldownChoices = { 30, 60, 120, 300, 600 };
            foreach (int cdSec in cooldownChoices)
            {
                string cdLabel = cdSec >= 60 ? $"{cdSec / 60} min" : $"{cdSec}s";
                _cmbCooldown.Items.Add(new ComboBoxItem { Content = $"every {cdLabel}", Tag = cdSec });
            }
            int cdIndex = Array.IndexOf(cooldownChoices, _engine.Config.CooldownSeconds);
            _cmbCooldown.SelectedIndex = cdIndex >= 0 ? cdIndex : 2;
            ToolTipService.SetToolTip(_cmbCooldown, "Minimum gap between Auto-Pilot sweeps. Doubles automatically (up to 30 min) while sweeps can't relieve memory pressure.");
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
            var g1 = CreateGuardTile("Protect active app", "Never trims the window you are using.", out _swProtectFg, _engine.Config.ProtectForeground, val =>
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
            var g2 = CreateGuardTile("Leak guard", "Flags apps whose private memory keeps growing (+120 MB/min) and trims them at most every 5 min.", out _swLeakGuard, _engine.Config.AutoLeakRemediation, val =>
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
            g3Text.Children.Add(new TextBlock { Text = "Every", FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = BrushHex("#F8FAFC") });
            _lblSchedCountdown = new TextBlock { Text = "off", FontSize = 10.5, Foreground = BrushHex("#94A3B8") };
            g3Text.Children.Add(_lblSchedCountdown);
            Grid.SetColumn(g3Text, 0);
            g3Grid.Children.Add(g3Text);

            _cmbSchedInterval = new ComboBox { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            _cmbSchedInterval.Items.Add(new ComboBoxItem { Content = "10m", Tag = 10 });
            _cmbSchedInterval.Items.Add(new ComboBoxItem { Content = "15m", Tag = 15 });
            _cmbSchedInterval.Items.Add(new ComboBoxItem { Content = "30m", Tag = 30 });
            _cmbSchedInterval.Items.Add(new ComboBoxItem { Content = "60m", Tag = 60 });
            int schedIndex = Array.IndexOf(new[] { 10, 15, 30, 60 }, _engine.Config.ScheduledIntervalMinutes);
            _cmbSchedInterval.SelectedIndex = schedIndex >= 0 ? schedIndex : 1;
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
            var g4 = CreateGuardTile("Start with Windows", _engine.IsAdmin ? "Starts elevated in the tray at logon." : "Starts in the tray at logon (standard user — run as Admin and re-enable to start elevated).", out _swStartup, _engine.Config.RunOnStartup, val =>
            {
                // schtasks can take a few seconds; keep the UI responsive.
                _swStartup.IsEnabled = false;
                RunEngineActionAsync(() => _engine.SetStartup(val), res =>
                {
                    ShowToast(res.Ok ? (val ? "Windows Startup Enabled" : "Windows Startup Disabled") : "Startup Not Changed", res.Message);
                    if (!res.Ok)
                    {
                        _isUpdatingUi = true;
                        _swStartup.IsOn = _engine.Config.RunOnStartup;
                        _isUpdatingUi = false;
                    }
                }, "startup", always: () => _swStartup.IsEnabled = true);
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
                ShowToast($"{label} preset", $"Trigger {pct}% · floor {minFree:F1} GB · {mode}");
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
            if (!string.IsNullOrEmpty(sub)) ToolTipService.SetToolTip(b, sub);
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
                Text = "TOP APPS",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#38BDF8")
            });
            hdr.Children.Add(titleStack);

            var openAllBtn = new Button
            {
                Content = "All →",
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
                Text = "ACTIVITY",
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
                PlaceholderText = "Search",
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
            _cmbSortProcs.Items.Add(new ComboBoxItem { Content = "Memory", Tag = "memory" });
            _cmbSortProcs.Items.Add(new ComboBoxItem { Content = "Score", Tag = "score" });
            _cmbSortProcs.Items.Add(new ComboBoxItem { Content = "CPU", Tag = "cpu" });
            _cmbSortProcs.Items.Add(new ComboBoxItem { Content = "Growth", Tag = "leak" });
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
                Content = "Apps",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                CornerRadius = new CornerRadius(8),
                Background = BrushHex("#38BDF8", 45),
                BorderBrush = BrushHex("#38BDF8"),
                BorderThickness = new Thickness(1)
            };
            _btnGroupPids = new Button
            {
                Content = "Processes",
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
            AddFilterButton(filterStack, "all", "All");
            AddFilterButton(filterStack, "idle", "Idle & leaks");
            AddFilterButton(filterStack, "governed", "Frozen & eco");
            AddFilterButton(filterStack, "protected", "Shielded & capped");

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
                Text = "PROFILES",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushHex("#38BDF8")
            });
            hdr.Children.Add(tStack);

            var rightHdr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            _swProfileAuto = new ToggleSwitch
            {
                OnContent = "Auto-detect",
                OffContent = "Manual",
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
                Content = "Use auto-detect",
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
                Text = "HOTKEYS",
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
            hk1Sp.Children.Add(new TextBlock { Text = "Hotkeys", FontWeight = FontWeights.Bold, FontSize = 12.5 });
            _swHotkeys = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsOn = _engine.Config.HotkeysEnabled };
            ToolTipService.SetToolTip(_swHotkeys, $"Edit keys in {System.IO.Path.Combine(NativeMemoryEngine.DataDirectory, "pulseram_config.json")}");
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
            hk2Sp.Children.Add(new TextBlock { Text = "Smart", FontWeight = FontWeights.Bold, FontSize = 12.5 });
            string hkPrimaryText = TryParseHotkey(_engine.Config.HotkeyPrimary, out _, out _, out string hkPrimaryLabel) ? hkPrimaryLabel : $"Invalid: {_engine.Config.HotkeyPrimary}";
            hk2Sp.Children.Add(new TextBlock { Text = hkPrimaryText, FontSize = 16, FontWeight = FontWeights.ExtraBold, Foreground = BrushHex("#38BDF8") });
            hkBox2.Child = hk2Sp;
            Grid.SetColumn(hkBox2, 1);
            hkRow.Children.Add(hkBox2);

            var hkBox3 = CreateCardBorder(new Thickness(12));
            var hk3Sp = new StackPanel { Spacing = 4 };
            hk3Sp.Children.Add(new TextBlock { Text = "Deep", FontWeight = FontWeights.Bold, FontSize = 12.5 });
            string hkSecondaryText = TryParseHotkey(_engine.Config.HotkeySecondary, out _, out _, out string hkSecondaryLabel) ? hkSecondaryLabel : $"Invalid: {_engine.Config.HotkeySecondary}";
            hk3Sp.Children.Add(new TextBlock { Text = hkSecondaryText, FontSize = 16, FontWeight = FontWeights.ExtraBold, Foreground = BrushHex("#A855F7") });
            hkBox3.Child = hk3Sp;
            Grid.SetColumn(hkBox3, 2);
            hkRow.Children.Add(hkBox3);

            hkStack.Children.Add(hkRow);
            hkCard.Child = hkStack;
            stack.Children.Add(hkCard);

            stack.Children.Add(BuildDeviceCard());

            scroll.Content = stack;
            outer.Children.Add(scroll);
            return outer;
        }

        /// <summary>Shows what PC PulseRAM tuned itself for, the UI weight, and a one-click re-tune.</summary>
        private Border BuildDeviceCard()
        {
            var card = CreateCardBorder();
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = "DEVICE", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = BrushHex("#38BDF8") });
            info.Children.Add(new TextBlock { Text = _engine.Device.Label, FontSize = 12, Foreground = BrushHex("#F8FAFC") });

            var opts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 4, 0, 0) };
            var chkTray = new CheckBox { Content = "Tray %", FontSize = 12, MinWidth = 0, IsChecked = _engine.Config.TrayShowsPercent };
            ToolTipService.SetToolTip(chkTray, "Show the live RAM % in the tray icon (green → amber → red with memory pressure).");
            chkTray.Click += (s, e) =>
            {
                _engine.Config.TrayShowsPercent = chkTray.IsChecked == true;
                _engine.SaveConfig();
                _trayIconKey = "";
                UpdateTrayTooltip(_engine.GetSnapshot());
            };
            var chkEco = new CheckBox { Content = "Eco in tray", FontSize = 12, MinWidth = 0, IsChecked = _engine.Config.EfficiencyModeWhenHidden };
            ToolTipService.SetToolTip(chkEco, "Run PulseRAM itself in Windows Efficiency Mode while it is hidden in the tray.");
            chkEco.Click += (s, e) =>
            {
                _engine.Config.EfficiencyModeWhenHidden = chkEco.IsChecked == true;
                _engine.SaveConfig();
            };
            var chkSmartPurge = new CheckBox { Content = "Smart purge", FontSize = 12, MinWidth = 0, IsChecked = _engine.Config.SmartStandbyPurge };
            ToolTipService.SetToolTip(chkSmartPurge, "Automatic sweeps only empty the standby cache when free memory is low or Windows is already evicting cache. Manual sweeps always purge.");
            chkSmartPurge.Click += (s, e) =>
            {
                _engine.Config.SmartStandbyPurge = chkSmartPurge.IsChecked == true;
                _engine.SaveConfig();
            };
            opts.Children.Add(chkTray);
            opts.Children.Add(chkEco);
            opts.Children.Add(chkSmartPurge);
            var chkCommit = new CheckBox { Content = "Memory warnings", FontSize = 12, MinWidth = 0, IsChecked = _engine.Config.CommitWarningEnabled };
            ToolTipService.SetToolTip(chkCommit, "Warn when committed memory nears the limit (RAM + page file) — what actually makes apps crash with \"out of memory\".");
            chkCommit.Click += (s, e) =>
            {
                _engine.Config.CommitWarningEnabled = chkCommit.IsChecked == true;
                _engine.SaveConfig();
            };
            opts.Children.Add(chkCommit);
            info.Children.Add(opts);
            row.Children.Add(info);

            var cmbUi = new ComboBox { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 };
            cmbUi.Items.Add(new ComboBoxItem { Content = "UI: Auto", Tag = "auto" });
            cmbUi.Items.Add(new ComboBoxItem { Content = "UI: Lite", Tag = "lite" });
            cmbUi.Items.Add(new ComboBoxItem { Content = "UI: Full", Tag = "full" });
            cmbUi.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "auto", "lite", "full" }, _engine.Config.UiMode));
            ToolTipService.SetToolTip(cmbUi, "Lite: no ambient animation, slower refresh and fewer rows — easier on low-end PCs. Auto picks Lite on low-end hardware.");
            cmbUi.SelectionChanged += (s, e) =>
            {
                if (cmbUi.SelectedItem is ComboBoxItem item && item.Tag is string mode)
                {
                    _engine.Config.UiMode = mode;
                    _engine.SaveConfig();
                    ApplyPerformanceMode();
                    RefreshAllUi(force: true);
                }
            };
            Grid.SetColumn(cmbUi, 2);
            row.Children.Add(cmbUi);

            var retune = new Button
            {
                Content = "Re-tune",
                FontSize = 12,
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(retune, "Reset the trigger, free-memory floor, cooldown and profile triggers to the recommended values for this PC.");
            retune.Click += (s, e) =>
            {
                _engine.RetuneForThisDevice();
                RefreshAllUi(force: true);
                RenderProfilesGrid();
                ShowToast("Tuned for this PC",
                    $"Trigger {_engine.Config.RamThresholdPercent}% · floor {_engine.Config.MinFreeRamGb:F2} GB · cooldown {_engine.Config.CooldownSeconds / 60.0:0.#} min");
            };
            Grid.SetColumn(retune, 3);
            row.Children.Add(retune);

            var cmbTheme = new ComboBox { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 };
            cmbTheme.Items.Add(new ComboBoxItem { Content = "Theme: System", Tag = "system" });
            cmbTheme.Items.Add(new ComboBoxItem { Content = "Theme: Dark", Tag = "dark" });
            cmbTheme.Items.Add(new ComboBoxItem { Content = "Theme: Light", Tag = "light" });
            cmbTheme.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "system", "dark", "light" }, _engine.Config.Theme));
            cmbTheme.SelectionChanged += (s, e) =>
            {
                if (cmbTheme.SelectedItem is ComboBoxItem item && item.Tag is string theme && theme != _engine.Config.Theme)
                {
                    _engine.Config.Theme = theme;
                    _engine.SaveConfig();
                    // Rebuild after this event returns (the combo box itself is part of the tree being replaced).
                    DispatcherQueue.TryEnqueue(ApplyTheme);
                }
            };
            Grid.SetColumn(cmbTheme, 1);
            row.Children.Add(cmbTheme);

            var logs = new Button { Content = "Logs", FontSize = 12, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(logs, "Open the log folder (useful when reporting a problem).");
            logs.Click += (s, e) => Log.OpenFolder();
            Grid.SetColumn(logs, 4);
            row.Children.Add(logs);

            card.Child = row;
            return card;
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
                }
                else
                {
                    // Back to the user's own size (or a size that fits the screen), never a fixed 1440×930.
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
            ToolTipService.SetToolTip(ringHost, "Drag to move · double-click for full view");
            return panel;
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
