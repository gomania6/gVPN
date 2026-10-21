using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;

namespace gVPN
{
    [SupportedOSPlatform("windows")]
    public partial class App : Application
    {
        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(System.IntPtr hInternet, int dwOption, System.IntPtr lpBuffer, int dwBufferLength);

        protected override void OnExit(ExitEventArgs e)
        {
            // Гарантированно отключаем системный прокси при закрытии приложения
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
                if (key != null)
                {
                    key.SetValue("ProxyEnable", 0);
                    InternetSetOption(System.IntPtr.Zero, 39, System.IntPtr.Zero, 0);
                    InternetSetOption(System.IntPtr.Zero, 37, System.IntPtr.Zero, 0);
                }
            }
            catch { }

            base.OnExit(e);
        }
    }
}