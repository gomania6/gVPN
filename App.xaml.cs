using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace gVPN
{
    [SupportedOSPlatform("windows")]
    public partial class App : Application
    {
        private const string MutexName = "Global\\gVPN_SingleInstance_Mutex";
        private const string ShowEventName = "Global\\gVPN_ShowWindow_Event";
        private const string ConnectEventName = "Global\\gVPN_Connect_Event";

        private Mutex? _instanceMutex;
        private EventWaitHandle? _showWindowEvent;
        private EventWaitHandle? _connectEvent;
        private bool _ownsMutex;

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        protected override void OnStartup(StartupEventArgs e)
        {
            _instanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
            _ownsMutex = isNewInstance;
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _connectEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ConnectEventName);

            bool hasAutoStart = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
            bool hasMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            bool hasConnect = e.Args.Any(a => a.Equals("--connect", StringComparison.OrdinalIgnoreCase));

            if (!isNewInstance)
            {
                if (hasConnect)
                    _connectEvent.Set();
                else
                    _showWindowEvent.Set();

                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Сбрасываем системный прокси, если он остался включённым после аварийного завершения
            CleanupStaleSystemProxy();

            var settings = SettingsService.LoadSettings();

            var mainWindow = new MainWindow(connectOnStartup: hasConnect);
            this.MainWindow = mainWindow;

            ListenForIpcSignals(mainWindow);

            if (settings.StartMinimized || hasAutoStart || hasMinimized)
            {
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.ShowInTaskbar = false;
                mainWindow.Show();
                mainWindow.Hide();
            }
            else
            {
                mainWindow.Show();
            }
        }

        private void ListenForIpcSignals(MainWindow mainWindow)
        {
            Task.Run(() =>
            {
                if (_showWindowEvent == null || _connectEvent == null) return;
                WaitHandle[] handles = { _showWindowEvent, _connectEvent };

                while (true)
                {
                    try
                    {
                        int signaled = WaitHandle.WaitAny(handles);
                        if (signaled == 0)
                        {
                            Dispatcher.Invoke(() => mainWindow.RestoreFromTray());
                        }
                        else if (signaled == 1)
                        {
                            Dispatcher.Invoke(() => mainWindow.TriggerConnect());
                        }
                    }
                    catch
                    {
                        break;
                    }
                }
            });
        }

        public static void CleanupStaleSystemProxy()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
                if (key == null) return;

                string? currentProxy = key.GetValue("ProxyServer")?.ToString();
                int proxyEnabled = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0);

                if (proxyEnabled == 1 && (string.IsNullOrEmpty(currentProxy) || currentProxy.StartsWith("127.0.0.1:108")))
                {
                    XrayService.DisableSystemProxy();
                }
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            PulseGuardService.WriteStatus(
                PulseGuardState.Disconnected,
                (this.MainWindow as MainWindow)?.CurrentProfileForStatus);

            CleanupStaleSystemProxy();

            try
            {
                _showWindowEvent?.Dispose();
                _connectEvent?.Dispose();
                if (_ownsMutex)
                    _instanceMutex?.ReleaseMutex();
                _instanceMutex?.Dispose();
            }
            catch { }

            base.OnExit(e);
        }
    }
}