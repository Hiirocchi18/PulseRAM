using System;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PulseRAM
{
    /// <summary>One short welcome card on a new install.</summary>
    public sealed partial class MainWindow
    {
        private bool _welcomeShowing;
        private CheckBox? _chkUpdates; // the Settings checkbox, kept in sync with the welcome card

        /// <summary>Shown once, on the first launch with no saved settings (never for upgrades or --tray starts).</summary>
        private async void ShowWelcomeIfFirstRun()
        {
            if (!_engine.IsFirstRun || _engine.Config.WelcomeShown || _welcomeShowing || _isMiniHud) return;
            _welcomeShowing = true;
            try
            {
                // The dialog needs a loaded visual tree.
                for (int i = 0; i < 40 && _rootGrid.XamlRoot == null; i++) await Task.Delay(50);
                if (_rootGrid.XamlRoot == null) return;

                var cfg = _engine.Config;
                var body = new StackPanel { Spacing = 12, MaxWidth = 440 };
                body.Children.Add(Para($"Your PC: {_engine.Device.Label}. PulseRAM has tuned itself for it.", bold: true));
                body.Children.Add(Para($"Auto-Pilot is on. When RAM use passes {cfg.RamThresholdPercent}% or free memory drops below {cfg.MinFreeRamGb:0.##} GB, " +
                                       "it trims apps you aren't using. The app you're working in is never touched, and nothing is closed or deleted."));
                body.Children.Add(Para("Clean now any time with the big Clean button, or Ctrl + Alt + Shift + M from anywhere."));
                if (!_engine.IsAdmin)
                    body.Children.Add(Para("For the deeper cleans (standby cache, page combining), click Admin at the top."));
                body.Children.Add(Para("Want it out of the way? The compact button next to Auto turns PulseRAM into a small always-on-top widget."));

                var chkStartup = new CheckBox { Content = "Start PulseRAM with Windows (in the tray)", IsChecked = _engine.IsStartupEnabled() };
                var chkUpdates = new CheckBox { Content = "Tell me when a new version is out (checks GitHub once a day)", IsChecked = cfg.CheckForUpdates };
                body.Children.Add(chkStartup);
                body.Children.Add(chkUpdates);

                var dialog = new ContentDialog
                {
                    Title = "Welcome to PulseRAM",
                    Content = body,
                    PrimaryButtonText = "Get started",
                    SecondaryButtonText = "Try compact view",
                    DefaultButton = ContentDialogButton.Primary,
                    RequestedTheme = s_lightTheme ? ElementTheme.Light : ElementTheme.Dark,
                    XamlRoot = _rootGrid.XamlRoot
                };

                ContentDialogResult result;
                try { result = await dialog.ShowAsync(); }
                catch (Exception ex)
                {
                    // Another dialog is open; try again next launch.
                    Log.Info($"Welcome card not shown: {ex.GetType().Name}");
                    return;
                }

                cfg.WelcomeShown = true;
                cfg.CheckForUpdates = chkUpdates.IsChecked == true;
                _engine.SaveConfig();
                if (_chkUpdates != null) _chkUpdates.IsChecked = cfg.CheckForUpdates;
                if ((chkStartup.IsChecked == true) != _engine.IsStartupEnabled())
                    _swStartup.IsOn = chkStartup.IsChecked == true; // the switch's handler registers startup
                if (result == ContentDialogResult.Secondary && !_isMiniHud) ToggleMiniHudMode();
            }
            catch (Exception ex)
            {
                Log.Warn("Welcome card failed", ex);
            }
            finally
            {
                _welcomeShowing = false;
            }
        }

        private static TextBlock Para(string text, bool bold = false) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
    }
}
