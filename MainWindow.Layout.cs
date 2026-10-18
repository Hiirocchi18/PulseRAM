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
            _updateBanner = BuildUpdateBanner();
            var banners = new StackPanel { Spacing = 8 };
            banners.Children.Add(_commitBanner);
            banners.Children.Add(_updateBanner);
            Grid.SetRow(banners, 1);
            _fullLayout.Children.Add(banners);

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

            _leakPanel = BuildLeakPanel();
            stack.Children.Add(_leakPanel);
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
            info.Children.Add(new TextBlock
            {
                Text = $"{_engine.Device.Label} · PulseRAM {UpdateChecker.CurrentVersionText}{(NativeMemoryEngine.IsPortable ? " (portable)" : "")}",
                FontSize = 12,
                Foreground = BrushHex("#F8FAFC")
            });

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
            var chkUpdates = new CheckBox { Content = "Updates", FontSize = 12, MinWidth = 0, IsChecked = _engine.Config.CheckForUpdates };
            ToolTipService.SetToolTip(chkUpdates, "Check GitHub for a new version once a day. It's the only network request PulseRAM makes, and it never downloads anything by itself.");
            chkUpdates.Click += (s, e) =>
            {
                _engine.Config.CheckForUpdates = chkUpdates.IsChecked == true;
                _engine.SaveConfig();
            };
            opts.Children.Add(chkUpdates);
            var checkNow = new HyperlinkButton { Content = "Check now", FontSize = 12, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            checkNow.Click += (s, e) => CheckForUpdates(manual: true);
            opts.Children.Add(checkNow);
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
    }
}
