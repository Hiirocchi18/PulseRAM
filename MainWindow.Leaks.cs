using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PulseRAM.Core;

namespace PulseRAM
{
    /// <summary>"Possible leaks": apps whose private memory kept growing, with Restart / Dismiss / Ignore.</summary>
    public sealed partial class MainWindow
    {
        private Border _leakPanel = null!;
        private StackPanel _leakRows = null!;
        private string _leakSignature = "";

        private Border BuildLeakPanel()
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 14, 10),
                Background = BrushHex("#F43F5E", 22),
                BorderBrush = BrushHex("#F43F5E", 110),
                Visibility = Visibility.Collapsed
            };
            var stack = new StackPanel { Spacing = 8 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = BrushHex("#F43F5E") });
            header.Children.Add(new TextBlock { Text = "POSSIBLE LEAKS", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = BrushHex("#F43F5E"), VerticalAlignment = VerticalAlignment.Center });
            var help = new FontIcon { Glyph = "", FontSize = 12, Foreground = BrushHex("#94A3B8"), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(help, "These apps' private memory kept growing for minutes. Trimming can't give leaked memory back — restarting the app does.");
            header.Children.Add(help);
            stack.Children.Add(header);
            _leakRows = new StackPanel { Spacing = 6 };
            stack.Children.Add(_leakRows);
            card.Child = stack;
            return card;
        }

        private void OnLeakDetected(LeakIncident incident)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RenderLeakList(force: true);
                string title = $"{incident.App} may be leaking memory";
                string body = $"+{incident.LastRateMbPerMin:F0} MB/min, {FormatMb(incident.PrivateMb)} now. Restarting it frees the memory (Processes tab).";
                if (IsDashboardVisible()) ShowToast(title, body);
                else ShowTrayBalloon($"PulseRAM: {title}", body, warning: true);
            });
        }

        /// <summary>Rebuilds the rows only when the list changed (called from the refresh loop).</summary>
        private void RenderLeakList(bool force = false)
        {
            var list = _engine.GetLeakIncidents();
            string sig = string.Join("|", list.Select(i => $"{i.App}:{i.LastSeen.Ticks / TimeSpan.TicksPerMinute}:{i.Episodes}"));
            if (!force && sig == _leakSignature) return;
            _leakSignature = sig;

            _leakRows.Children.Clear();
            _leakPanel.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var inc in list.Take(6))
                _leakRows.Children.Add(BuildLeakRow(inc));
        }

        private Grid BuildLeakRow(LeakIncident inc)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = inc.App, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = BrushHex("#F8FAFC") });
            string when = inc.FirstSeen.Date == DateTime.Today ? $"{inc.FirstSeen:HH:mm}" : $"{inc.FirstSeen:MMM d HH:mm}";
            string meta = $"up to +{inc.PeakRateMbPerMin:F0} MB/min · {FormatMb(inc.PrivateMb)} · since {when}";
            if (inc.Episodes > 1) meta += $" · {inc.Episodes} times";
            text.Children.Add(new TextBlock { Text = meta, FontSize = 11.5, Foreground = BrushHex("#94A3B8"), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);

            var restart = SmallButton("Restart", "Asks the app to close (so it can save your work), then starts it again. PulseRAM never force-closes it.");
            restart.Click += (s, e) =>
            {
                restart.IsEnabled = false;
                RunEngineActionAsync(() => _engine.RestartApp(inc.App), r =>
                {
                    ShowToast(r.Ok ? "Restart" : "Couldn't restart", r.Message);
                    RenderLeakList(force: true);
                }, "restart:" + inc.App, always: () => restart.IsEnabled = true);
            };
            Grid.SetColumn(restart, 1);
            row.Children.Add(restart);

            var dismiss = SmallButton("Dismiss", "Remove from this list. It comes back if the app starts growing again.");
            dismiss.Click += (s, e) => { _engine.DismissLeak(inc.App); RenderLeakList(force: true); };
            Grid.SetColumn(dismiss, 2);
            row.Children.Add(dismiss);

            var ignore = SmallButton("Ignore", "Never list this app again (it is still trimmed by the leak guard if that's on).");
            ignore.Click += (s, e) => { _engine.IgnoreLeak(inc.App); RenderLeakList(force: true); };
            Grid.SetColumn(ignore, 3);
            row.Children.Add(ignore);
            return row;
        }

        private static Button SmallButton(string text, string tip)
        {
            var b = new Button
            {
                Content = text,
                FontSize = 12,
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(7),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(b, tip);
            return b;
        }
    }
}
