using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace PulseRAM
{
    public static class Program
    {
        private static Mutex? _singleInstanceMutex;
        private static PulseRamApp? _appInstance;
        public static MainWindow? ActiveMainWindow;

        /// <summary>True when the previous session did not shut down cleanly (crash, kill, power loss).</summary>
        public static bool PreviousSessionCrashed { get; private set; }

        /// <summary>Broadcast by a second launch so the running instance shows its window.</summary>
        public const string ShowWindowMessageName = "PulseRAM.ShowMainWindow.v1";

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        /// <summary>Broadcast by the installer so a running (possibly elevated) instance thaws apps and exits cleanly.</summary>
        public const string ExitForInstallerMessageName = "PulseRAM.ExitForInstaller.v1";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private const int ASFW_ANY = -1;

        private static bool HasArg(string[] args, string name) =>
            args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Already running (often hidden in the tray): bring that window forward instead of exiting silently.
        /// A --tray launch (e.g. a leftover startup entry) stays silent.
        /// </summary>
        private static void SignalExistingInstance(string[] args)
        {
            if (HasArg(args, "--tray")) return;
            try
            {
                // We are the foreground process (the user just launched us); hand that right to the running
                // instance so its SetForegroundWindow actually works instead of only flashing the taskbar.
                AllowSetForegroundWindow(ASFW_ANY);
                uint msg = RegisterWindowMessage(ShowWindowMessageName);
                if (msg != 0) PostMessage(HWND_BROADCAST, msg, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            args ??= Array.Empty<string>();
            // Elevated: ignore junctions planted by non-admin programs (Windows 11 22H2+), and take the current
            // directory out of the DLL search order. (Restricting the search further with SetDefaultDllDirectories
            // breaks WinUI's own loading, so it isn't used.)
            SafeFile.EnableRedirectionGuard();
            if (InstallSecurity.IsElevated)
            {
                try { SetDllDirectory(""); } catch { }
            }

            // Used by the installer before upgrading/uninstalling: resume anything PulseRAM suspended.
            if (HasArg(args, "--thaw-frozen"))
            {
                NativeMemoryEngine.RecoverFrozenProcesses();
                return;
            }

            bool createdNew;
            try
            {
                _singleInstanceMutex = new Mutex(true, @"Local\PulseRAM_WinUI3_SingleInstance", out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                // An elevated instance owns the mutex and its default DACL denies us: it is running.
                SignalExistingInstance(args);
                return;
            }

            if (!createdNew)
            {
                if (HasArg(args, "--elevated-relaunch"))
                {
                    // The non-elevated instance that launched us is closing; wait for it to release the mutex.
                    try
                    {
                        if (!_singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(15)))
                            return;
                    }
                    catch (AbandonedMutexException)
                    {
                        // Previous instance exited without releasing it; we now own the mutex.
                    }
                }
                else
                {
                    SignalExistingInstance(args);
                    return;
                }
            }

            PreviousSessionCrashed = Log.MarkSessionStart();
            if (PreviousSessionCrashed) Log.Warn("Previous session ended without a clean shutdown");

            // Crash logging: background-thread exceptions are fatal in .NET; at least record why.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log.Error($"Fatal unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error("Unobserved task exception", e.Exception);
                e.SetObserved();
            };

            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _appInstance = new PulseRamApp(args);
            });

            GC.KeepAlive(_appInstance);
            GC.KeepAlive(ActiveMainWindow);
            GC.KeepAlive(_singleInstanceMutex);
        }
    }

    public sealed class PulseRamApp : Application, IXamlMetadataProvider
    {
        private readonly string[] _args;
        private readonly XamlControlsXamlMetaDataProvider _metadataProvider = new XamlControlsXamlMetaDataProvider();

        public PulseRamApp(string[] args)
        {
            _args = args;
            RequestedTheme = ApplicationTheme.Dark;
            UnhandledException += (s, e) =>
            {
                // Keep running, but never silently: log it and let the user know where to look.
                Log.Error($"UI exception: {e.Message}", e.Exception);
                e.Handled = true;
                Program.ActiveMainWindow?.ReportUnexpectedError(e.Message);
            };
        }

        public IXamlType GetXamlType(Type type) => _metadataProvider.GetXamlType(type);
        public IXamlType GetXamlType(string fullName) => _metadataProvider.GetXamlType(fullName);
        public XmlnsDefinition[] GetXmlnsDefinitions() => _metadataProvider.GetXmlnsDefinitions();

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            Program.ActiveMainWindow = new MainWindow(_args);
            Program.ActiveMainWindow.Activate();
            Program.ActiveMainWindow.InitializeNativeHooksAfterActivate();
        }
    }
}
