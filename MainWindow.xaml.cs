using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace gVPN
{
    [SupportedOSPlatform("windows")]
    public partial class MainWindow : Window
    {
        private readonly XrayService _xrayService = new();
        private ObservableCollection<VpnProfile> _profiles = new();
        private ObservableCollection<string> _targetApps = new();
        private AppSettings _settings = new();
        private VpnProfile? _activeProfile;
        private bool _isConnected = false;
        private bool _isLoadingSettings = false;

        private readonly DispatcherTimer _speedTimer;
        private NetworkInterface[]? _cachedInterfaces;
        private long _lastBytesReceived = 0;
        private long _lastBytesSent = 0;

        private NotifyIcon? _notifyIcon;
        private bool _isRealExit = false;
        private ContextMenu? _trayMenu;
        private MenuItem? _trayStatusItem;
        private MenuItem? _traySpeedItem;
        private MenuItem? _trayConnectItem;

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

        public MainWindow()
        {
            string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(exeDir))
                Directory.SetCurrentDirectory(exeDir);

            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            InitializeComponent();
            _xrayService.EnsureCoreExtracted();
            SetupTrayIcon();

            _profiles = new ObservableCollection<VpnProfile>(SettingsService.LoadProfiles());
            LstServers.ItemsSource = _profiles;

            LoadSettingsUi();

            _speedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _speedTimer.Tick += SpeedTimer_Tick;

            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TrimMemory();

            string[] args = Environment.GetCommandLineArgs();
            bool hasAutoStart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

            if (_settings.StartMinimized || hasAutoStart)
                this.Hide();

            if (_settings.AutoStartConnect && LstServers.SelectedItem != null)
            {
                await Task.Delay(800);
                BtnConnect_Click(BtnConnect, new RoutedEventArgs());
            }
        }

        private static void TrimMemory()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
            }
            catch { }
        }

        // ================= ТРЕЙ =================

        private void SetupTrayIcon()
        {
            Icon? appIcon = null;
            try
            {
                string? p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p))
                    appIcon = System.Drawing.Icon.ExtractAssociatedIcon(p);
            }
            catch { }

            _notifyIcon = new NotifyIcon
            {
                Visible = true,
                Text = "gVPN Client",
                Icon = appIcon ?? System.Drawing.SystemIcons.Application
            };

            _trayMenu = new ContextMenu { Style = (Style)FindResource("TrayMenuStyle") };

            _trayStatusItem = new MenuItem { Header = "gVPN: Отключено", IsEnabled = false, Style = (Style)FindResource("TrayMenuItemStyle") };
            _traySpeedItem = new MenuItem { Header = "Скорость: —", IsEnabled = false, Visibility = Visibility.Collapsed, Style = (Style)FindResource("TrayMenuItemStyle") };
            _trayConnectItem = new MenuItem { Header = "Подключиться", Style = (Style)FindResource("TrayMenuItemStyle") };
            _trayConnectItem.Click += (s, e) => BtnConnect_Click(BtnConnect, new RoutedEventArgs());

            var showItem = new MenuItem { Header = "Открыть окно", Style = (Style)FindResource("TrayMenuItemStyle") };
            showItem.Click += (s, e) => RestoreFromTray();

            var exitItem = new MenuItem
            {
                Header = "Выйти из gVPN",
                Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F38BA8")),
                Style = (Style)FindResource("TrayMenuItemStyle")
            };
            exitItem.Click += (s, e) => { _isRealExit = true; this.Close(); };

            _trayMenu.Items.Add(_trayStatusItem);
            _trayMenu.Items.Add(_traySpeedItem);
            _trayMenu.Items.Add(new Separator { Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#313244")) });
            _trayMenu.Items.Add(_trayConnectItem);
            _trayMenu.Items.Add(showItem);
            _trayMenu.Items.Add(new Separator { Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#313244")) });
            _trayMenu.Items.Add(exitItem);

            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) RestoreFromTray();
                else if (e.Button == MouseButtons.Right) _trayMenu.IsOpen = true;
            };
        }

        private void RestoreFromTray()
        {
            ShowMainView();
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void MinimizeToTray()
        {
            this.Hide();
            TrimMemory();
        }

        // ================= НАВИГАЦИЯ =================

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (e.ChangedButton == System.Windows.Input.MouseButton.Left) this.DragMove(); }
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) { this.WindowState = WindowState.Minimized; TrimMemory(); }
        private void BtnClose_Click(object sender, RoutedEventArgs e) => MinimizeToTray();

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isRealExit)
            {
                e.Cancel = true;
                MinimizeToTray();
            }
            else
            {
                _xrayService.Stop();
                if (_notifyIcon != null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); }
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (PnlSettingsView.Visibility == Visibility.Visible) ShowMainView();
            else ShowSettingsView();
        }

        private void BtnBackToServers_Click(object sender, RoutedEventArgs e) => ShowMainView();

        private void ShowMainView()
        {
            PnlSettingsView.Visibility = Visibility.Collapsed;
            PnlMainView.Visibility = Visibility.Visible;
            BtnSettings.Content = "⚙";
            TxtHeaderTitle.Text = "gVPN";
        }

        private void ShowSettingsView()
        {
            PnlMainView.Visibility = Visibility.Collapsed;
            PnlSettingsView.Visibility = Visibility.Visible;
            BtnSettings.Content = "✕";
            TxtHeaderTitle.Text = _settings.Language == "en" ? "Settings" : (_settings.Language == "pl" ? "Ustawienia" : "Настройки");
        }

        // ================= НАСТРОЙКИ =================

        private void LoadSettingsUi()
        {
            _isLoadingSettings = true;
            _settings = SettingsService.LoadSettings();

            ChkAutoStartApp.IsChecked = _settings.AutoStartApp;
            ChkAutoStartConnect.IsChecked = _settings.AutoStartConnect;
            ChkStartMinimized.IsChecked = _settings.StartMinimized;

            RbTunnelAll.IsChecked = !_settings.TunnelOnlySelectedApps;
            RbTunnelApps.IsChecked = _settings.TunnelOnlySelectedApps;
            PnlAppsList.Visibility = _settings.TunnelOnlySelectedApps ? Visibility.Visible : Visibility.Collapsed;

            _targetApps = new ObservableCollection<string>(_settings.TargetAppPaths ?? new());
            LstApps.ItemsSource = _targetApps;

            foreach (ComboBoxItem item in CmbLanguage.Items)
            {
                if (item.Tag?.ToString() == _settings.Language)
                {
                    CmbLanguage.SelectedItem = item;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(_settings.LastSelectedProfileId))
            {
                var p = _profiles.FirstOrDefault(x => x.Id == _settings.LastSelectedProfileId);
                if (p != null) LstServers.SelectedItem = p;
                else if (_profiles.Count > 0) LstServers.SelectedIndex = 0;
            }
            else if (_profiles.Count > 0)
            {
                LstServers.SelectedIndex = 0;
            }

            LocalizationService.UpdateUi(this, _settings.Language, _isConnected, _activeProfile?.Name);
            _isLoadingSettings = false;
        }

        private void SaveSettingsFromUi()
        {
            // Если вызов произошёл до полной загрузки окна
            if (ChkAutoStartApp == null || RbTunnelApps == null)
                return;

            _settings.AutoStartApp = ChkAutoStartApp.IsChecked == true;
            _settings.AutoStartConnect = ChkAutoStartConnect.IsChecked == true;
            _settings.StartMinimized = ChkStartMinimized.IsChecked == true;
            _settings.TunnelOnlySelectedApps = RbTunnelApps.IsChecked == true;
            _settings.TargetAppPaths = _targetApps.ToList();

            if (LstServers.SelectedItem is VpnProfile p)
                _settings.LastSelectedProfileId = p.Id;

            SettingsService.SaveSettings(_settings);
        }

        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (!_isLoadingSettings) SaveSettingsFromUi();
        }

        private void OnRoutingModeChanged(object sender, RoutedEventArgs e)
        {
            // Защита от вызова во время InitializeComponent(), когда контролы ещё не созданы
            if (_isLoadingSettings || RbTunnelApps == null || PnlAppsList == null)
                return;

            bool onlyApps = RbTunnelApps.IsChecked == true;
            PnlAppsList.Visibility = onlyApps ? Visibility.Visible : Visibility.Collapsed;
            SaveSettingsFromUi();
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingSettings) return;
            if (CmbLanguage.SelectedItem is ComboBoxItem item && item.Tag is string lang)
            {
                _settings.Language = lang;
                LocalizationService.UpdateUi(this, lang, _isConnected, _activeProfile?.Name);
                SaveSettingsFromUi();
            }
        }

        private void BtnAddApp_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Исполняемые файлы (*.exe)|*.exe",
                Title = "Выберите .exe"
            };

            if (ofd.ShowDialog() == true && !_targetApps.Contains(ofd.FileName))
            {
                _targetApps.Add(ofd.FileName);
                SaveSettingsFromUi();
            }
        }

        private void BtnRemoveApp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is string path)
            {
                _targetApps.Remove(path);
                SaveSettingsFromUi();
            }
        }

        private void BtnLaunchApp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is string path && File.Exists(path))
            {
                try
                {
                    ProcessStartInfo psi = new(path)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(path) ?? ""
                    };

                    psi.Arguments = "--proxy-server=\"socks5://127.0.0.1:10808\"";

                    // Переменные окружения для остальных приложений и утилит
                    psi.EnvironmentVariables["HTTP_PROXY"] = "http://127.0.0.1:10809";
                    psi.EnvironmentVariables["HTTPS_PROXY"] = "http://127.0.0.1:10809";
                    psi.EnvironmentVariables["ALL_PROXY"] = "socks5://127.0.0.1:10808";

                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка запуска: {ex.Message}");
                }
            }
        }

        // ================= СЕРВЕРЫ =================

        private void BtnToggleAddPanel_Click(object sender, RoutedEventArgs e)
        {
            bool show = PnlAddServer.Visibility != Visibility.Visible;
            PnlAddServer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            BtnToggleAddPanel.Content = show ? "Скрыть форму" : "+ Добавить";
            if (show) TxtInput.Focus();
        }

        private async void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            string input = TxtInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(input)) return;

            BtnAdd.IsEnabled = false;
            try
            {
                if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    string raw = await client.GetStringAsync(input);
                    string decoded = raw;
                    try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Trim())); } catch { }

                    foreach (var line in decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.Trim().StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                            AddVless(line.Trim());
                    }
                }
                else if (input.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                {
                    AddVless(input);
                }
                else if (input.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
                {
                    var p = new VpnProfile { Name = $"WireGuard #{_profiles.Count + 1}", Type = "wireguard", RawData = input };
                    _profiles.Add(p);
                    SettingsService.SaveProfiles(_profiles);
                    LstServers.SelectedItem = p;
                }

                TxtInput.Clear();
                PnlAddServer.Visibility = Visibility.Collapsed;
                BtnToggleAddPanel.Content = "+ Добавить";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
            finally { BtnAdd.IsEnabled = true; }
        }

        private void AddVless(string url)
        {
            string name = $"VLESS #{_profiles.Count + 1}";
            try
            {
                var uri = new Uri(url);
                name = !string.IsNullOrEmpty(uri.Fragment) ? HttpUtility.UrlDecode(uri.Fragment.TrimStart('#')) : $"{uri.Host}:{uri.Port}";
            }
            catch { }

            var p = new VpnProfile { Name = name, Type = "vless", RawData = url };
            _profiles.Add(p);
            SettingsService.SaveProfiles(_profiles);
            LstServers.SelectedItem = p;
        }

        private void BtnDeleteServer_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected) return;
            if (sender is FrameworkElement el && el.DataContext is VpnProfile p)
            {
                _profiles.Remove(p);
                SettingsService.SaveProfiles(_profiles);
                if (_profiles.Count > 0) LstServers.SelectedIndex = 0;
            }
        }

        private void LstServers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isConnected && _activeProfile != null && LstServers.SelectedItem != _activeProfile)
            {
                LstServers.SelectedItem = _activeProfile;
                return;
            }

            if (LstServers.SelectedItem is VpnProfile p)
            {
                TxtStatus.Text = $"Выбран: {p.Name} ({p.Type.ToUpper()})";
                _settings.LastSelectedProfileId = p.Id;
                SaveSettingsFromUi();
            }
        }

        // ================= ПОДКЛЮЧЕНИЕ =================

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                if (LstServers.SelectedItem is not VpnProfile p) return;

                _activeProfile = p;
                BtnConnect.IsEnabled = false;
                TxtStatus.Text = "Запуск службы...";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Orange;

                try
                {
                    // 1. Запускаем ядро Xray
                    _xrayService.Start(p, !_settings.TunnelOnlySelectedApps);

                    // Даем 1 секунду убедиться, что процесс не упал мгновенно
                    await Task.Delay(1000);

                    if (!_xrayService.IsRunning)
                    {
                        throw new Exception("Процесс xray.exe завершился сразу после старта. Проверьте конфиг.");
                    }

                    // 2. Мгновенно переключаем кнопку и статус в активное состояние
                    _isConnected = true;
                    BtnConnect.Content = "Отключиться";
                    BtnConnect.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F38BA8"));
                    TxtStatus.Text = $"Подключено: {p.Name}";
                    TxtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;

                    if (_trayConnectItem != null) _trayConnectItem.Header = "Отключиться";
                    if (_trayStatusItem != null) _trayStatusItem.Header = $"Подключено: {p.Name}";
                    if (_traySpeedItem != null) _traySpeedItem.Visibility = Visibility.Visible;

                    _cachedInterfaces = NetworkInterface.GetAllNetworkInterfaces();
                    _lastBytesReceived = 0;
                    _lastBytesSent = 0;
                    PnlSpeed.Visibility = Visibility.Visible;
                    _speedTimer.Start();
                    TrimMemory();
                }
                catch (Exception ex)
                {
                    _xrayService.Stop();
                    TxtStatus.Text = "Ошибка запуска";
                    TxtStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                    MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    // Кнопка ОБЯЗАТЕЛЬНО разблокируется в любом случае
                    BtnConnect.IsEnabled = true;
                }
            }
            else
            {
                // Отключение туннеля
                _xrayService.Stop();
                _speedTimer.Stop();
                _isConnected = false;
                _activeProfile = null;
                PnlSpeed.Visibility = Visibility.Collapsed;

                BtnConnect.Content = "Подключиться";
                BtnConnect.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#89B4FA"));
                TxtStatus.Text = "Статус: Отключено";
                TxtStatus.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#BAC2DE"));

                if (_trayConnectItem != null) _trayConnectItem.Header = "Подключиться";
                if (_trayStatusItem != null) _trayStatusItem.Header = "gVPN: Отключено";
                if (_traySpeedItem != null) _traySpeedItem.Visibility = Visibility.Collapsed;
                TrimMemory();
            }
        }

        private void SpeedTimer_Tick(object? sender, EventArgs e)
        {
            if (_cachedInterfaces == null) return;
            long r = 0, s = 0;

            foreach (var ni in _cachedInterfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    try
                    {
                        var stats = ni.GetIPStatistics();
                        r += stats.BytesReceived;
                        s += stats.BytesSent;
                    }
                    catch { }
                }
            }

            if (_lastBytesReceived > 0 && _lastBytesSent > 0)
            {
                double d = Math.Max(0, r - _lastBytesReceived) / 1048576.0;
                double u = Math.Max(0, s - _lastBytesSent) / 1048576.0;

                TxtDownloadSpeed.Text = $"↓ {d:F2} МБ/с";
                TxtUploadSpeed.Text = $"↑ {u:F2} МБ/с";

                if (_traySpeedItem != null && _traySpeedItem.Visibility == Visibility.Visible)
                    _traySpeedItem.Header = $"Скорость: ↓ {d:F1} МБ/с | ↑ {u:F1} МБ/с";
            }

            _lastBytesReceived = r;
            _lastBytesSent = s;
        }
    }
}