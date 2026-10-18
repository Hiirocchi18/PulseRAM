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
    /// <summary>Daily update check and the "new version" banner.</summary>
    public sealed partial class MainWindow
    {
        private Border _updateBanner = null!;
        private TextBlock _updateBannerTitle = null!;
        private UpdateInfo? _pendingUpdate;
        private DispatcherQueueTimer? _updateTimer;
        private bool _updateCheckRunning;

        private Border BuildUpdateBanner()
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 8, 10, 8),
                Background = BrushHex("#38BDF8", 28),
                BorderBrush = BrushHex("#38BDF8", 130),
                Visibility = Visibility.Collapsed
            };
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            g.Children.Add(new FontIcon { Glyph = "\uE896", FontSize = 16, Foreground = BrushHex("#38BDF8"), VerticalAlignment = VerticalAlignment.Center });
            _updateBannerTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#F8FAFC"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(_updateBannerTitle, 1);
            g.Children.Add(_updateBannerTitle);

            var download = new Button { Content = "Download", FontSize = 12, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(download, "Opens the release page on GitHub. Download the installer there and run it — your settings are kept.");
            download.Click += (s, e) => OpenUpdatePage();
            Grid.SetColumn(download, 2);
            g.Children.Add(download);

            var skip = new Button { Content = "Skip this version", FontSize = 12, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Colors.Transparent) };
            skip.Click += (s, e) =>
            {
                if (_pendingUpdate != null)
                {
                    _engine.Config.SkippedUpdateVersion = _pendingUpdate.Version;
                    _engine.SaveConfig();
                }
                HideUpdateBanner();
            };
            Grid.SetColumn(skip, 3);
            g.Children.Add(skip);

            var dismiss = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }, Padding = new Thickness(8), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(dismiss, "Remind me later");
            dismiss.Click += (s, e) => HideUpdateBanner();
            Grid.SetColumn(dismiss, 4);
            g.Children.Add(dismiss);

            b.Child = g;
            return b;
        }

        private void HideUpdateBanner()
        {
            _pendingUpdate = null;
            _updateBanner.Visibility = Visibility.Collapsed;
        }

        private void OpenUpdatePage()
        {
            string url = _pendingUpdate?.PageUrl ?? $"https://github.com/{UpdateChecker.Repo}/releases/latest";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("Could not open the release page", ex); }
        }

        private void ShowUpdateBanner(UpdateInfo info, bool announce)
        {
            _pendingUpdate = info;
            _updateBannerTitle.Text = $"PulseRAM {info.Version} is available (you have {UpdateChecker.CurrentVersionText})";
            _updateBanner.Visibility = Visibility.Visible;
            if (!announce) return;
            if (_isMiniHud) ShowToast($"Update {info.Version} available", "Open the full view to download it.");
            else if (!IsDashboardVisible()) ShowTrayBalloon($"PulseRAM {info.Version} is available", "Open PulseRAM to download it.", warning: false);
        }

        /// <summary>First check shortly after start, then every few hours (each check itself is limited to once a day).</summary>
        private void StartUpdateChecks()
        {
            _updateTimer = DispatcherQueue.CreateTimer();
            _updateTimer.Interval = TimeSpan.FromSeconds(30);
            _updateTimer.IsRepeating = true;
            _updateTimer.Tick += (s, e) =>
            {
                // Wait for the user's answer on the welcome card (it has the opt-out).
                if (_welcomeShowing) return;
                // Every 4 h after a check; after a failed attempt (offline at logon, rate limit) retry in 30 min.
                _updateTimer.Interval = TimeSpan.FromHours(4);
                if (UpdateChecker.IsDue(_engine.Config, DateTime.UtcNow)) CheckForUpdates(manual: false);
            };
            _updateTimer.Start();
        }

        private async void CheckForUpdates(bool manual)
        {
            if (_updateCheckRunning) return;
            _updateCheckRunning = true;
            try
            {
                var (answered, info) = await UpdateChecker.CheckAsync(_engine.Config, ignoreSkipped: manual);
                if (answered)
                {
                    _engine.Config.LastUpdateCheckUtc = DateTime.UtcNow;
                    _engine.SaveConfig();
                }
                else if (!manual && _updateTimer != null)
                {
                    _updateTimer.Interval = TimeSpan.FromMinutes(30);
                }
                if (info != null) ShowUpdateBanner(info, announce: true);
                else if (manual) ShowToast(answered ? "You're up to date" : "Couldn't check for updates",
                    answered ? $"PulseRAM {UpdateChecker.CurrentVersionText} is the latest release." : "GitHub couldn't be reached. Try again later.");
            }
            catch (Exception ex)
            {
                Log.Warn("Update check failed", ex);
            }
            finally
            {
                _updateCheckRunning = false;
            }
        }
    }
}
