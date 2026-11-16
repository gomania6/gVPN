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

        private Mutex? _instanceMutex;
        private EventWaitHandle? _showWindowEvent;

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        protected override void OnStartup(StartupEventArgs e)
        {
            _instanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

            if (!isNewInstance)
            {
                _showWindowEvent.Set();
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Сбрасываем системный прокси, если он остался включённым после аварийного завершения
            CleanupStaleSystemProxy();

            var settings = SettingsService.LoadSettings();
            bool hasAutoStart = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

            var mainWindow = new MainWindow();
            this.MainWindow = mainWindow;

            ListenForShowSignal(mainWindow);

            if (settings.StartMinimized || hasAutoStart)
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

        private void ListenForShowSignal(MainWindow mainWindow)
        {
            Task.Run(() =>
            {
                while (_showWindowEvent != null)
                {
                    try
                    {
                        if (_showWindowEvent.WaitOne())
                        {
                            Dispatcher.Invoke(() => mainWindow.RestoreFromTray());
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
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
                    InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
                }
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            CleanupStaleSystemProxy();

            try
            {
                _showWindowEvent?.Dispose();
                _instanceMutex?.ReleaseMutex();
                _instanceMutex?.Dispose();
            }
            catch { }

            base.OnExit(e);
        }
    }
}