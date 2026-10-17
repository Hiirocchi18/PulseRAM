using System;
using System.IO;
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

        [STAThread]
        public static void Main(string[] args)
        {
            _singleInstanceMutex = new Mutex(true, @"Local\PulseRAM_WinUI3_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                return;
            }

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
                e.Handled = true;
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
