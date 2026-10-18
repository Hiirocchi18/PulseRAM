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
                try { Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "SystemPropertiesPerformance.exe")) { UseShellExecute = true }); }
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
    }
}
