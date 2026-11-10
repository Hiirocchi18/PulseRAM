//! Desktop integration outside the Slint windows: tray icon, global hotkeys, the hidden message window that
//! receives them, native popup menus, single-instance signalling and a few window tweaks.
//! Non-Windows development builds get no-op versions.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ShellEvent {
    /// A second launch asked the running instance to show its window.
    ShowWindow,
    /// The installer asked PulseRAM to thaw everything and exit.
    ExitRequested,
    Hotkey(i32),
    TrayOpen,
    TrayMenu,
    /// The dashboard's minimize button (or Win+Down …) was used and minimize-to-tray is on.
    MinimizeToTray,
    /// Windows is restarting or signing out.
    SessionEnding,
    /// Windows switched between light and dark.
    ColorsChanged,
    /// Explorer restarted: the tray icon has to be added again.
    TaskbarCreated,
}

pub struct MenuItem {
    pub id: u32,
    pub text: String,
    pub checked: bool,
}

impl MenuItem {
    pub fn new(id: u32, text: impl Into<String>) -> MenuItem {
        MenuItem { id, text: text.into(), checked: false }
    }

    pub fn checked(id: u32, text: impl Into<String>, checked: bool) -> MenuItem {
        MenuItem { id, text: text.into(), checked }
    }

    pub fn separator() -> MenuItem {
        MenuItem { id: 0, text: String::new(), checked: false }
    }
}

/// Rectangle in physical pixels.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Area {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

pub enum Instance {
    /// This is the only PulseRAM; carry on.
    Primary,
    /// Another PulseRAM is running (it has been asked to show its window unless this is a --tray start).
    AlreadyRunning,
}

/// Broadcast by a second launch so the running instance shows its window.
const SHOW_WINDOW_MESSAGE: &str = "PulseRAM.ShowMainWindow.v1";
/// Broadcast by the installer so a running (possibly elevated) instance thaws apps and exits cleanly.
const EXIT_FOR_INSTALLER_MESSAGE: &str = "PulseRAM.ExitForInstaller.v1";
// The name PulseRAM 1.x used, so an old and a new version never run side by side during an upgrade.
const SINGLE_INSTANCE_MUTEX: &str = r"Local\PulseRAM_WinUI3_SingleInstance";

// 3×5 pixel digits, one string per row ('#' = lit).
const PIXEL_DIGITS: [[&str; 5]; 10] = [
    ["###", "#.#", "#.#", "#.#", "###"],
    [".#.", "##.", ".#.", ".#.", "###"],
    ["###", "..#", "###", "#..", "###"],
    ["###", "..#", "###", "..#", "###"],
    ["#.#", "#.#", "###", "..#", "..#"],
    ["###", "#..", "###", "..#", "###"],
    ["###", "#..", "###", "#.#", "###"],
    ["###", "..#", "..#", "..#", "..#"],
    ["###", "#.#", "###", "#.#", "###"],
    ["###", "#.#", "###", "..#", "###"],
];

/// Draws the RAM percentage as a crisp pixel-font badge (rounded square coloured by memory pressure).
/// Returns `size * size` pixels as 0xAARRGGBB, top row first.
pub fn percent_badge_pixels(percent: u32, background_rgb: u32, size: usize, dark_text: bool) -> Vec<u32> {
    let background = 0xFF00_0000 | (background_rgb & 0x00FF_FFFF);
    let foreground: u32 = if dark_text { 0xFF11_1827 } else { 0xFFFF_FFFF };
    let mut pixels = vec![0u32; size * size];

    let radius = (size / 5).max(2) as f64;
    let edge = size as f64;
    for y in 0..size {
        for x in 0..size {
            // Distance from the nearest corner-circle centre; only matters inside the corner squares.
            let (fx, fy) = (x as f64 + 0.5, y as f64 + 0.5);
            let qx = if fx < radius { radius - fx } else if fx > edge - radius { fx - (edge - radius) } else { 0.0 };
            let qy = if fy < radius { radius - fy } else if fy > edge - radius { fy - (edge - radius) } else { 0.0 };
            if qx * qx + qy * qy <= radius * radius {
                pixels[y * size + x] = background;
            }
        }
    }

    let text = percent.min(99).to_string();
    let digits = text.len();
    let scale = ((size.saturating_sub(2)) / (digits * 4 - 1)).min(size.saturating_sub(2) / 5).max(1);
    let text_width = (digits * 4 - 1) * scale;
    let text_height = 5 * scale;
    let origin_x = size.saturating_sub(text_width) / 2;
    let origin_y = size.saturating_sub(text_height) / 2;
    for (index, digit) in text.bytes().enumerate() {
        let glyph = &PIXEL_DIGITS[(digit - b'0') as usize];
        for (gy, row) in glyph.iter().enumerate() {
            for (gx, cell) in row.bytes().enumerate() {
                if cell != b'#' {
                    continue;
                }
                for sy in 0..scale {
                    for sx in 0..scale {
                        let x = origin_x + (index * 4 + gx) * scale + sx;
                        let y = origin_y + gy * scale + sy;
                        if x < size && y < size {
                            pixels[y * size + x] = foreground;
                        }
                    }
                }
            }
        }
    }
    pixels
}

#[cfg(windows)]
mod imp {
    use std::cell::{Cell, RefCell};
    use std::ffi::c_void;
    use std::mem::{size_of, zeroed};
    use std::ptr::{null, null_mut};
    use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering};

    use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, ERROR_ACCESS_DENIED, ERROR_ALREADY_EXISTS, HWND, LPARAM, LRESULT, POINT, RECT, WAIT_ABANDONED, WPARAM};
    use windows_sys::Win32::Graphics::Dwm::DwmSetWindowAttribute;
    use windows_sys::Win32::Graphics::Gdi::{
        CreateBitmap, CreateDIBSection, DeleteObject, EnumDisplayMonitors, EnumDisplaySettingsExW, GetMonitorInfoW, MonitorFromPoint, BITMAPINFO,
        BITMAPINFOHEADER, DEVMODEW, ENUM_CURRENT_SETTINGS, HDC, HMONITOR, MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONULL, MONITOR_DEFAULTTOPRIMARY,
    };
    use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleW, SetDllDirectoryW};
    use windows_sys::Win32::System::Threading::{CreateMutexW, WaitForSingleObject};
    use windows_sys::Win32::UI::HiDpi::{GetDpiForSystem, GetDpiForWindow, GetSystemMetricsForDpi};
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{RegisterHotKey, UnregisterHotKey, MOD_NOREPEAT};
    use windows_sys::Win32::UI::Shell::{
        DefSubclassProc, RemoveWindowSubclass, SetWindowSubclass, Shell_NotifyIconW, NIF_ICON, NIF_INFO, NIF_MESSAGE, NIF_TIP, NIIF_INFO, NIIF_WARNING,
        NIM_ADD, NIM_DELETE, NIM_MODIFY, NIN_BALLOONUSERCLICK, NOTIFYICONDATAW,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        AllowSetForegroundWindow, AppendMenuW, ChangeWindowMessageFilterEx, CreateIconIndirect, CreatePopupMenu, CreateWindowExW, DefWindowProcW,
        DestroyIcon, DestroyMenu, DestroyWindow, GetCursorPos, GetWindowLongPtrW, LoadIconW, LoadImageW, PostMessageW, RegisterClassW,
        RegisterWindowMessageW, SendMessageW, SetForegroundWindow, SetLayeredWindowAttributes, SetWindowLongPtrW, SetWindowPos, TrackPopupMenu, ASFW_ANY,
        GWL_EXSTYLE, HICON, HWND_BROADCAST, ICONINFO, IDI_APPLICATION, IMAGE_ICON, LR_SHARED, LWA_ALPHA, MF_CHECKED, MF_SEPARATOR, MF_STRING, MSGFLT_ALLOW,
        SC_MINIMIZE, SM_CXICON, SM_CXSMICON, SWP_FRAMECHANGED, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SWP_NOZORDER, TPM_NONOTIFY, TPM_RETURNCMD, WM_APP,
        WM_ENDSESSION, WM_HOTKEY, WM_LBUTTONDBLCLK, WM_LBUTTONUP, WM_NCDESTROY, WM_NULL, WM_RBUTTONUP, WM_SETICON, WM_SETTINGCHANGE, WM_SYSCOMMAND,
        WNDCLASSW, WS_EX_LAYERED,
    };

    use super::{percent_badge_pixels, Area, Instance, MenuItem, ShellEvent, EXIT_FOR_INSTALLER_MESSAGE, SHOW_WINDOW_MESSAGE, SINGLE_INSTANCE_MUTEX};
    use crate::log;
    use crate::sys::wide;

    const WM_TRAY: u32 = WM_APP + 1;
    const WM_MINIMIZE_TO_TRAY: u32 = WM_APP + 2;
    const TRAY_ID: u32 = 1001;
    const SUBCLASS_ID: usize = 0x5052; // "PR"

    static HELPER: AtomicIsize = AtomicIsize::new(0);
    static MINIMIZE_TO_TRAY: AtomicBool = AtomicBool::new(false);

    struct Messages {
        show_window: u32,
        exit_for_installer: u32,
        taskbar_created: u32,
    }

    #[derive(Default)]
    struct Tray {
        added: bool,
        /// Live RAM % icon currently shown (owned by us).
        badge_icon: isize,
        /// percent|colour|size|text of the badge being shown.
        badge_key: String,
    }

    thread_local! {
        static HANDLER: Cell<Option<fn(ShellEvent)>> = const { Cell::new(None) };
        static MESSAGES: Cell<(u32, u32, u32)> = const { Cell::new((0, 0, 0)) };
        static TRAY: RefCell<Tray> = RefCell::new(Tray::default());
    }

    fn helper() -> HWND {
        HELPER.load(Ordering::Relaxed) as HWND
    }

    fn dispatch(event: ShellEvent) {
        if let Some(handler) = HANDLER.with(Cell::get) {
            handler(event);
        }
    }

    unsafe extern "system" fn helper_proc(hwnd: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
        let (show_window, exit_for_installer, taskbar_created) = MESSAGES.with(Cell::get);
        match message {
            WM_TRAY => {
                match (lparam as u32) & 0xFFFF {
                    WM_LBUTTONUP | WM_LBUTTONDBLCLK | NIN_BALLOONUSERCLICK => dispatch(ShellEvent::TrayOpen),
                    WM_RBUTTONUP => dispatch(ShellEvent::TrayMenu),
                    _ => {}
                }
                0
            }
            WM_HOTKEY => {
                dispatch(ShellEvent::Hotkey(wparam as i32));
                0
            }
            WM_MINIMIZE_TO_TRAY => {
                dispatch(ShellEvent::MinimizeToTray);
                0
            }
            WM_ENDSESSION => {
                if wparam != 0 {
                    // The process is about to end without a normal close: thaw frozen apps and save state now.
                    dispatch(ShellEvent::SessionEnding);
                }
                0
            }
            WM_SETTINGCHANGE => {
                if lparam != 0 && crate::sys::from_wide_ptr(lparam as *const u16) == "ImmersiveColorSet" {
                    dispatch(ShellEvent::ColorsChanged);
                }
                DefWindowProcW(hwnd, message, wparam, lparam)
            }
            _ if message == show_window && show_window != 0 => {
                dispatch(ShellEvent::ShowWindow);
                0
            }
            _ if message == exit_for_installer && exit_for_installer != 0 => {
                dispatch(ShellEvent::ExitRequested);
                0
            }
            _ if message == taskbar_created && taskbar_created != 0 => {
                dispatch(ShellEvent::TaskbarCreated);
                0
            }
            _ => DefWindowProcW(hwnd, message, wparam, lparam),
        }
    }

    /// Creates the hidden top-level window that receives tray clicks, hotkeys and broadcasts. It lives for the
    /// whole session, independent of the dashboard and widget windows coming and going.
    pub fn init(handler: fn(ShellEvent)) -> bool {
        HANDLER.with(|h| h.set(Some(handler)));
        unsafe {
            let instance = GetModuleHandleW(null());
            let class_name = wide("PulseRAM.MessageWindow");
            let mut class: WNDCLASSW = zeroed();
            class.lpfnWndProc = Some(helper_proc);
            class.hInstance = instance;
            class.lpszClassName = class_name.as_ptr();
            RegisterClassW(&class);
            let hwnd = CreateWindowExW(0, class_name.as_ptr(), wide("PulseRAM").as_ptr(), 0, 0, 0, 0, 0, null_mut(), null_mut(), instance, null());
            if hwnd.is_null() {
                log::warn(format!("Could not create the message window (error {}); tray and hotkeys are unavailable", GetLastError()));
                return false;
            }
            HELPER.store(hwnd as isize, Ordering::Relaxed);

            // Allow the broadcasts through UIPI in case this instance is elevated and the sender is not.
            let register = |name: &str| {
                let message = RegisterWindowMessageW(wide(name).as_ptr());
                if message != 0 {
                    ChangeWindowMessageFilterEx(hwnd, message, MSGFLT_ALLOW, null_mut());
                }
                message
            };
            let messages = Messages {
                show_window: register(SHOW_WINDOW_MESSAGE),
                exit_for_installer: register(EXIT_FOR_INSTALLER_MESSAGE),
                taskbar_created: register("TaskbarCreated"),
            };
            MESSAGES.with(|m| m.set((messages.show_window, messages.exit_for_installer, messages.taskbar_created)));
        }
        true
    }

    pub fn destroy() {
        let hwnd = HELPER.swap(0, Ordering::Relaxed) as HWND;
        if !hwnd.is_null() {
            unsafe { DestroyWindow(hwnd) };
        }
    }

    // ---- Tray icon ----

    fn app_icon(size: i32) -> HICON {
        unsafe {
            // Resource 1 is PulseRAM.ico (see build.rs).
            let icon = LoadImageW(GetModuleHandleW(null()), 1 as *const u16, IMAGE_ICON, size, size, LR_SHARED) as HICON;
            if icon.is_null() {
                LoadIconW(null_mut(), IDI_APPLICATION)
            } else {
                icon
            }
        }
    }

    /// Small-icon size for the tray at the system DPI (16 px at 100%, 24 at 150%, 32 at 200%).
    fn tray_icon_size() -> i32 {
        unsafe { GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem().max(96)).clamp(16, 64) }
    }

    fn copy_text(target: &mut [u16], text: &str) {
        let units: Vec<u16> = text.encode_utf16().collect();
        let n = units.len().min(target.len() - 1);
        target[..n].copy_from_slice(&units[..n]);
        target[n] = 0;
    }

    fn notify_data(flags: u32) -> NOTIFYICONDATAW {
        let mut data: NOTIFYICONDATAW = unsafe { zeroed() };
        data.cbSize = size_of::<NOTIFYICONDATAW>() as u32;
        data.hWnd = helper();
        data.uID = TRAY_ID;
        data.uFlags = flags;
        data.uCallbackMessage = WM_TRAY;
        data
    }

    pub fn tray_add() -> bool {
        if helper().is_null() {
            return false;
        }
        let mut data = notify_data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        data.hIcon = app_icon(tray_icon_size());
        copy_text(&mut data.szTip, "PulseRAM");
        let added = unsafe { Shell_NotifyIconW(NIM_ADD, &data) != 0 };
        TRAY.with(|tray| {
            let mut tray = tray.borrow_mut();
            tray.added = added;
            tray.badge_key.clear();
        });
        if added {
            log::info("Tray icon added");
        } else {
            log::warn(format!("Tray icon could not be added (error {}); minimize goes to the taskbar", unsafe { GetLastError() }));
        }
        added
    }

    pub fn tray_ready() -> bool {
        TRAY.with(|tray| tray.borrow().added)
    }

    fn create_badge_icon(percent: u32, rgb: u32, size: i32, dark_text: bool) -> HICON {
        let pixels = percent_badge_pixels(percent, rgb, size as usize, dark_text);
        unsafe {
            let mut info: BITMAPINFO = zeroed();
            info.bmiHeader.biSize = size_of::<BITMAPINFOHEADER>() as u32;
            info.bmiHeader.biWidth = size;
            info.bmiHeader.biHeight = -size; // top-down
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            let mut bits: *mut c_void = null_mut();
            let color = CreateDIBSection(null_mut(), &info, 0, &mut bits, null_mut(), 0);
            if color.is_null() || bits.is_null() {
                return null_mut();
            }
            std::ptr::copy_nonoverlapping(pixels.as_ptr(), bits as *mut u32, pixels.len());
            // Monochrome AND-mask, all zero: the colour bitmap's alpha channel carries transparency.
            let mask_stride = ((size as usize + 15) / 16) * 2;
            let mask_bits = vec![0u8; mask_stride * size as usize];
            let mask = CreateBitmap(size, size, 1, 1, mask_bits.as_ptr() as *const c_void);
            let icon_info = ICONINFO { fIcon: 1, xHotspot: 0, yHotspot: 0, hbmMask: mask, hbmColor: color };
            let icon = CreateIconIndirect(&icon_info);
            DeleteObject(color);
            if !mask.is_null() {
                DeleteObject(mask);
            }
            icon
        }
    }

    /// Updates the tooltip and, when `badge` is given, draws the live RAM % (percent, colour, dark text) into
    /// the icon. Without a badge the app icon is shown.
    pub fn tray_update(tip: &str, badge: Option<(u32, u32, bool)>) {
        TRAY.with(|tray| {
            let mut tray = tray.borrow_mut();
            if !tray.added {
                return;
            }
            let mut data = notify_data(NIF_TIP);
            copy_text(&mut data.szTip, tip);

            match badge {
                Some((percent, rgb, dark_text)) => {
                    let size = tray_icon_size();
                    let key = format!("{percent}|{rgb:06x}|{size}|{dark_text}");
                    if key != tray.badge_key {
                        let icon = create_badge_icon(percent, rgb, size, dark_text);
                        if !icon.is_null() {
                            data.uFlags |= NIF_ICON;
                            data.hIcon = icon;
                            unsafe {
                                if Shell_NotifyIconW(NIM_MODIFY, &data) != 0 {
                                    if tray.badge_icon != 0 {
                                        DestroyIcon(tray.badge_icon as HICON);
                                    }
                                    tray.badge_icon = icon as isize;
                                    tray.badge_key = key;
                                } else {
                                    DestroyIcon(icon);
                                }
                            }
                            return;
                        }
                    }
                }
                None if tray.badge_icon != 0 => {
                    // Switched off: go back to the app icon.
                    data.uFlags |= NIF_ICON;
                    data.hIcon = app_icon(tray_icon_size());
                    unsafe {
                        Shell_NotifyIconW(NIM_MODIFY, &data);
                        DestroyIcon(tray.badge_icon as HICON);
                    }
                    tray.badge_icon = 0;
                    tray.badge_key.clear();
                    return;
                }
                None => {}
            }
            unsafe { Shell_NotifyIconW(NIM_MODIFY, &data) };
        });
    }

    pub fn tray_balloon(title: &str, text: &str, warning: bool) {
        if !tray_ready() {
            return;
        }
        let mut data = notify_data(NIF_INFO);
        copy_text(&mut data.szInfoTitle, title);
        let mut body = text.to_string();
        if body.chars().count() > 255 {
            body = body.chars().take(252).collect::<String>() + "…";
        }
        copy_text(&mut data.szInfo, &body);
        data.dwInfoFlags = if warning { NIIF_WARNING } else { NIIF_INFO };
        unsafe { Shell_NotifyIconW(NIM_MODIFY, &data) };
    }

    pub fn tray_remove() {
        TRAY.with(|tray| {
            let mut tray = tray.borrow_mut();
            if tray.added {
                unsafe { Shell_NotifyIconW(NIM_DELETE, &notify_data(0)) };
                tray.added = false;
            }
            if tray.badge_icon != 0 {
                unsafe { DestroyIcon(tray.badge_icon as HICON) };
                tray.badge_icon = 0;
            }
            tray.badge_key.clear();
        });
    }

    // ---- Hotkeys ----

    pub fn register_hotkey(id: i32, modifiers: u32, vk: u32) -> bool {
        !helper().is_null() && unsafe { RegisterHotKey(helper(), id, modifiers | MOD_NOREPEAT, vk) != 0 }
    }

    pub fn unregister_hotkey(id: i32) {
        if !helper().is_null() {
            unsafe { UnregisterHotKey(helper(), id) };
        }
    }

    // ---- Menus ----

    /// Shows a native popup menu at the mouse pointer and returns the chosen item's id (0 = dismissed).
    pub fn popup_menu(items: &[MenuItem]) -> u32 {
        let owner = helper();
        if owner.is_null() {
            return 0;
        }
        unsafe {
            let menu = CreatePopupMenu();
            if menu.is_null() {
                return 0;
            }
            for item in items {
                if item.id == 0 {
                    AppendMenuW(menu, MF_SEPARATOR, 0, null());
                } else {
                    AppendMenuW(menu, MF_STRING | if item.checked { MF_CHECKED } else { 0 }, item.id as usize, wide(&item.text).as_ptr());
                }
            }
            let mut point = POINT { x: 0, y: 0 };
            GetCursorPos(&mut point);
            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(owner);
            let command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY, point.x, point.y, 0, owner, null());
            PostMessageW(owner, WM_NULL, 0, 0);
            DestroyMenu(menu);
            command.max(0) as u32
        }
    }

    // ---- Windows ----

    /// The Win32 handle of a Slint window, once it exists.
    pub fn window_handle(window: &slint::Window) -> Option<isize> {
        use raw_window_handle::{HasWindowHandle, RawWindowHandle};
        let handle = window.window_handle();
        match handle.window_handle().ok()?.as_raw() {
            RawWindowHandle::Win32(win32) => Some(win32.hwnd.get()),
            _ => None,
        }
    }

    unsafe extern "system" fn minimize_hook(hwnd: HWND, message: u32, wparam: WPARAM, lparam: LPARAM, _id: usize, _data: usize) -> LRESULT {
        // Minimize button, taskbar click, Alt+Space → Minimize: hide to the tray instead (option).
        if message == WM_SYSCOMMAND && (wparam & 0xFFF0) == SC_MINIMIZE as usize && MINIMIZE_TO_TRAY.load(Ordering::Relaxed) && !helper().is_null() {
            PostMessageW(helper(), WM_MINIMIZE_TO_TRAY, 0, 0);
            return 0;
        }
        if message == WM_NCDESTROY {
            RemoveWindowSubclass(hwnd, Some(minimize_hook), SUBCLASS_ID);
        }
        DefSubclassProc(hwnd, message, wparam, lparam)
    }

    /// Gives a freshly created window its icon and title-bar colours; the dashboard also gets the
    /// minimize-to-tray hook.
    pub fn attach_window(hwnd: isize, dark: bool, hook_minimize: bool) {
        let hwnd = hwnd as HWND;
        unsafe {
            let dpi = GetDpiForWindow(hwnd).max(96);
            SendMessageW(hwnd, WM_SETICON, 0, app_icon(GetSystemMetricsForDpi(SM_CXSMICON, dpi)) as LPARAM);
            SendMessageW(hwnd, WM_SETICON, 1, app_icon(GetSystemMetricsForDpi(SM_CXICON, dpi)) as LPARAM);
            if hook_minimize {
                SetWindowSubclass(hwnd, Some(minimize_hook), SUBCLASS_ID, 0);
            }
        }
        set_title_bar_dark(hwnd as isize, dark);
    }

    pub fn set_minimize_to_tray(enabled: bool) {
        MINIMIZE_TO_TRAY.store(enabled, Ordering::Relaxed);
    }

    pub fn set_title_bar_dark(hwnd: isize, dark: bool) {
        let hwnd = hwnd as HWND;
        let value: i32 = i32::from(dark);
        unsafe {
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, &value as *const _ as *const c_void, size_of::<i32>() as u32);
            // Redraw the frame: on a live theme switch the title bar otherwise keeps the old colours until the
            // window is re-activated.
            SetWindowPos(hwnd, null_mut(), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    pub fn bring_to_front(hwnd: isize) {
        unsafe { SetForegroundWindow(hwnd as HWND) };
    }

    /// Whole-window opacity via a layered window; `None` returns to normal rendering.
    pub fn set_window_alpha(hwnd: isize, alpha: Option<u8>) {
        let hwnd = hwnd as HWND;
        unsafe {
            let style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            let layered = WS_EX_LAYERED as isize;
            match alpha {
                None => {
                    if style & layered != 0 {
                        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style & !layered);
                    }
                }
                Some(alpha) => {
                    if style & layered == 0 {
                        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style | layered);
                    }
                    SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
                }
            }
        }
    }

    fn work_area(flags: u32, x: i32, y: i32) -> Option<Area> {
        unsafe {
            let monitor = MonitorFromPoint(POINT { x, y }, flags);
            if monitor.is_null() {
                return None;
            }
            let mut info: MONITORINFO = zeroed();
            info.cbSize = size_of::<MONITORINFO>() as u32;
            if GetMonitorInfoW(monitor, &mut info) == 0 {
                return None;
            }
            let r = info.rcWork;
            Some(Area { x: r.left, y: r.top, width: r.right - r.left, height: r.bottom - r.top })
        }
    }

    /// Work area of the screen that contains this point, if any screen does.
    pub fn work_area_at(x: i32, y: i32) -> Option<Area> {
        work_area(MONITOR_DEFAULTTONULL, x, y)
    }

    pub fn primary_work_area() -> Area {
        work_area(MONITOR_DEFAULTTOPRIMARY, 0, 0).unwrap_or(Area { x: 0, y: 0, width: 1280, height: 720 })
    }

    unsafe extern "system" fn refresh_rate_probe(monitor: HMONITOR, _dc: HDC, _area: *mut RECT, data: LPARAM) -> i32 {
        let all_reported = &mut *(data as *mut bool);
        let mut info: MONITORINFOEXW = zeroed();
        info.monitorInfo.cbSize = size_of::<MONITORINFOEXW>() as u32;
        if GetMonitorInfoW(monitor, &mut info as *mut _ as *mut MONITORINFO) != 0 {
            let mut mode: DEVMODEW = zeroed();
            mode.dmSize = size_of::<DEVMODEW>() as u16;
            if EnumDisplaySettingsExW(info.szDevice.as_ptr(), ENUM_CURRENT_SETTINGS, &mut mode, 0) != 0 && mode.dmDisplayFrequency == 0 {
                *all_reported = false;
            }
        }
        1
    }

    /// False if a connected display reports a refresh rate of 0 Hz (some virtual and remote displays do). The
    /// UI toolkit paces its drawing by that number and cannot open a window on such a display, so PulseRAM
    /// stays in the tray there instead.
    pub fn displays_report_refresh_rate() -> bool {
        let mut all_reported = true;
        unsafe { EnumDisplayMonitors(null_mut(), null(), Some(refresh_rate_probe), &mut all_reported as *mut bool as LPARAM) };
        all_reported
    }

    // ---- Process-wide ----

    /// Elevated: take the current directory out of the DLL search order.
    pub fn harden_dll_search() {
        if crate::sys::is_elevated() {
            unsafe { SetDllDirectoryW(wide("").as_ptr()) };
        }
    }

    /// Already running (often hidden in the tray): bring that window forward instead of exiting silently.
    fn signal_existing_instance() {
        unsafe {
            // We are the foreground process (the user just launched us); hand that right to the running
            // instance so its SetForegroundWindow actually works instead of only flashing the taskbar.
            AllowSetForegroundWindow(ASFW_ANY);
            let message = RegisterWindowMessageW(wide(SHOW_WINDOW_MESSAGE).as_ptr());
            if message != 0 {
                PostMessageW(HWND_BROADCAST, message, 0, 0);
            }
        }
    }

    /// Takes the single-instance mutex (kept for the life of the process). `wait_for_previous` is for the
    /// elevated relaunch: the non-elevated instance that started us is closing, so wait for it. A `quiet`
    /// start (--tray, e.g. a leftover startup entry) doesn't pop the running instance's window up.
    pub fn acquire_single_instance(wait_for_previous: bool, quiet: bool) -> Instance {
        unsafe {
            let mutex = CreateMutexW(null(), 1, wide(SINGLE_INSTANCE_MUTEX).as_ptr());
            let error = GetLastError();
            if mutex.is_null() {
                // ERROR_ACCESS_DENIED: an elevated instance owns the mutex and its default DACL denies us.
                if error == ERROR_ACCESS_DENIED && !quiet {
                    signal_existing_instance();
                }
                return Instance::AlreadyRunning;
            }
            if error == ERROR_ALREADY_EXISTS {
                if wait_for_previous {
                    let waited = WaitForSingleObject(mutex, 15_000);
                    // WAIT_ABANDONED: the previous instance exited without releasing it; we now own the mutex.
                    if waited == 0 || waited == WAIT_ABANDONED {
                        return Instance::Primary;
                    }
                    CloseHandle(mutex);
                    return Instance::AlreadyRunning;
                }
                if !quiet {
                    signal_existing_instance();
                }
                CloseHandle(mutex);
                return Instance::AlreadyRunning;
            }
            // Deliberately never closed: Windows releases it when the process ends.
            Instance::Primary
        }
    }
}

#[cfg(not(windows))]
mod imp {
    use super::{Area, Instance, MenuItem, ShellEvent};

    pub fn init(_handler: fn(ShellEvent)) -> bool {
        true
    }
    pub fn destroy() {}
    pub fn tray_add() -> bool {
        std::env::var_os("PULSERAM_FAKE_TRAY").is_some()
    }
    pub fn tray_ready() -> bool {
        std::env::var_os("PULSERAM_FAKE_TRAY").is_some()
    }
    pub fn tray_update(_tip: &str, _badge: Option<(u32, u32, bool)>) {}
    pub fn tray_balloon(_title: &str, _text: &str, _warning: bool) {}
    pub fn tray_remove() {}
    pub fn register_hotkey(_id: i32, _modifiers: u32, _vk: u32) -> bool {
        true
    }
    pub fn unregister_hotkey(_id: i32) {}
    pub fn popup_menu(_items: &[MenuItem]) -> u32 {
        0
    }
    pub fn window_handle(_window: &slint::Window) -> Option<isize> {
        Some(1)
    }
    pub fn attach_window(_hwnd: isize, _dark: bool, _hook_minimize: bool) {}
    pub fn set_minimize_to_tray(_enabled: bool) {}
    pub fn set_title_bar_dark(_hwnd: isize, _dark: bool) {}
    pub fn bring_to_front(_hwnd: isize) {}
    pub fn set_window_alpha(_hwnd: isize, _alpha: Option<u8>) {}
    pub fn work_area_at(x: i32, y: i32) -> Option<Area> {
        (x >= 0 && y >= 0 && x < 1920 && y < 1080).then_some(Area { x: 0, y: 0, width: 1920, height: 1080 })
    }
    pub fn primary_work_area() -> Area {
        Area { x: 0, y: 0, width: 1920, height: 1080 }
    }
    pub fn displays_report_refresh_rate() -> bool {
        true
    }
    pub fn harden_dll_search() {}
    pub fn acquire_single_instance(_wait_for_previous: bool, _quiet: bool) -> Instance {
        Instance::Primary
    }
}

pub use imp::*;

#[cfg(test)]
mod tests {
    use super::*;

    fn lit(pixels: &[u32], colour: u32) -> usize {
        pixels.iter().filter(|p| **p == colour).count()
    }

    #[test]
    fn badge_has_rounded_corners_and_centred_digits() {
        let size = 16;
        let pixels = percent_badge_pixels(47, 0x059669, size, false);
        assert_eq!(pixels.len(), size * size);
        assert_eq!(pixels[0], 0, "corner pixel is transparent");
        assert_eq!(pixels[size * 8], 0xFF05_9669, "edge pixel is the pressure colour");
        // "4" lights 9 cells and "7" lights 7 in the 3×5 font; at 16 px each cell is 2×2.
        assert_eq!(lit(&pixels, 0xFFFF_FFFF), (9 + 7) * 4);
        // The digits sit in the middle: nothing lit in the top two or bottom two rows.
        assert_eq!(lit(&pixels[..size * 2], 0xFFFF_FFFF) + lit(&pixels[size * 14..], 0xFFFF_FFFF), 0);
    }

    #[test]
    fn badge_scales_and_supports_dark_text() {
        let big = percent_badge_pixels(8, 0xF59E0B, 32, true);
        // One digit, scale 6: "8" lights 13 cells.
        assert_eq!(lit(&big, 0xFF11_1827), 13 * 36);
        let capped = percent_badge_pixels(250, 0xE11D48, 16, false);
        assert_eq!(lit(&capped, 0xFFFF_FFFF), (12 + 12) * 4, "values above 99 show as 99");
    }
}
