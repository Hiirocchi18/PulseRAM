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
            try
            {
                // Also tell DWM, and redraw the frame: on a live theme switch the title bar otherwise keeps
                // the old colours until the window is re-activated.
                int dark = s_lightTheme ? 0 : 1;
                DwmSetWindowAttribute(_hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020 /* NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED */);
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

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
                if (_pendingUpdate != null) ShowUpdateBanner(_pendingUpdate, announce: false);
                _leakSignature = "";
                RenderLeakList(force: true);
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
    }
}
