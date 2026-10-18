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
                if (_trayInitialized) Log.Info("Tray icon added");
                else Log.Warn($"Tray icon could not be added (error {Marshal.GetLastWin32Error()}); minimize goes to the taskbar");
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
        private static bool TryParseHotkey(string? text, out uint modifiers, out uint vk, out string label) =>
            PulseRAM.Core.HotkeyParser.TryParse(text, out modifiers, out vk, out label);

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

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        private const uint WM_SYSCOMMAND = 0x0112;
        private const long SC_MINIMIZE = 0xF020;

        /// <summary>Hides the window to the tray (it keeps running; the tray icon or a second launch brings it back).</summary>
        private void HideToTray()
        {
            try
            {
                // Remember "maximized" now: the debounced placement save won't run once the window is hidden,
                // and the tray click restores with SW_SHOWMAXIMIZED only if this is set.
                if (!_isMiniHud && !IsIconic(_hwnd) && _engine.Config.WindowMaximized != IsZoomed(_hwnd))
                {
                    _engine.Config.WindowMaximized = IsZoomed(_hwnd);
                    _engine.SaveConfig();
                }
                ShowWindow(_hwnd, 0 /* SW_HIDE */);
                RefreshAllUi(); // pauses rendering and switches to Efficiency Mode while hidden
                if (!_engine.Config.TrayHintShown)
                {
                    _engine.Config.TrayHintShown = true;
                    _engine.SaveConfig();
                    ShowTrayBalloon("PulseRAM is still running", "It's in the system tray. Click the icon to open it. You can change this under Profiles → Device → Minimize to tray.", warning: false);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Hide to tray failed", ex);
            }
        }

        private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // Minimize button, taskbar click, Alt+Space → Minimize: hide to the tray instead (option).
            if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE && _engine.Config.MinimizeToTray && _trayInitialized)
            {
                HideToTray();
                return IntPtr.Zero;
            }
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
    }
}
