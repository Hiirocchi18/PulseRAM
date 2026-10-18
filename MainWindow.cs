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
    public sealed partial class MainWindow : Window
    {

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
            _engine.OnLeakDetected += OnLeakDetected;

            // Remember size/position (debounced).
            _placementTimer = DispatcherQueue.CreateTimer();
            _placementTimer.Interval = TimeSpan.FromSeconds(1);
            _placementTimer.IsRepeating = false;
            _placementTimer.Tick += (s, e) => SaveWindowPlacement();
            _appWindow.Changed += (s, e) =>
            {
                // Other ways of minimizing (Win+Down, Win+M, "Show desktop" doesn't count) end up here.
                if (e.DidPresenterChange && _engine.Config.MinimizeToTray && _trayInitialized &&
                    _appWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Minimized)
                {
                    DispatcherQueue.TryEnqueue(HideToTray);
                }
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
                StartUpdateChecks();

                if (_launchArgs.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)))
                {
                    ShowWindow(_hwnd, 0);
                    RefreshAllUi(); // pauses animations while hidden
                }
                else
                {
                    ShowMainWindow();
                    // --compact: open straight into the compact widget (e.g. from a shortcut). The welcome card
                    // waits for a normal launch, since it doesn't fit in the widget.
                    if (_launchArgs.Any(a => string.Equals(a, "--compact", StringComparison.OrdinalIgnoreCase)))
                        ToggleMiniHudMode();
                    else
                        ShowWelcomeIfFirstRun();
                }

                if (!string.IsNullOrEmpty(_hotkeyStatus))
                    ShowToast("Hotkey unavailable", _hotkeyStatus);
                else if (NativeMemoryEngine.PortableUnavailable)
                    ShowToast("Portable mode is off", "This folder can't be written to, so settings are kept in %LocalAppData%\\PulseRAM. Move the folder somewhere writable to keep them next to the EXE.");
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

    }
}
