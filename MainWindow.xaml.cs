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
        private VpnProfile? _renamingProfile;
        private bool _isPinging = false;
        private VpnProfile? _lastAddedProfile;
        private readonly DispatcherTimer _autoPingTimer;
        private bool _isConnected = false;
        private bool _isLoadingSettings = false;
        private readonly bool _connectOnStartup;
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
        private MenuItem? _trayOpenItem;
        private MenuItem? _trayExitItem;

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

        public VpnProfile? CurrentProfileForStatus => _activeProfile ?? LstServers.SelectedItem as VpnProfile;

        public MainWindow(bool connectOnStartup = false)
        {
            _connectOnStartup = connectOnStartup;
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
            PulseGuardService.WriteStatus(PulseGuardState.Disconnected, LstServers.SelectedItem as VpnProfile);

            _speedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _speedTimer.Tick += SpeedTimer_Tick;
            _autoPingTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _autoPingTimer.Tick += AutoPingTimer_Tick;
            _autoPingTimer.Start();
            this.Loaded += MainWindow_Loaded;
        }

        public void TriggerConnect()
        {
            if (!_isConnected && BtnConnect.IsEnabled && LstServers.SelectedItem is VpnProfile)
            {
                BtnConnect_Click(BtnConnect, new RoutedEventArgs());
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _ = RunPingAllAsync();
            TrimMemory();
            if ((_settings.AutoStartConnect || _connectOnStartup) && LstServers.SelectedItem != null && !_isConnected)
            {
                await Task.Delay(800);
                TriggerConnect();
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

        // ================= СИСТЕМНЫЙ ТРЕЙ =================
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
            _traySpeedItem = new MenuItem { Header = "↓ 0.0 MB/s  ↑ 0.0 MB/s", IsEnabled = false, Visibility = Visibility.Collapsed, Style = (Style)FindResource("TrayMenuItemStyle") };

            _trayConnectItem = new MenuItem { Header = "Подключить", Style = (Style)FindResource("TrayMenuItemStyle") };
            _trayConnectItem.Click += (s, e) => BtnConnect_Click(BtnConnect, new RoutedEventArgs());

            _trayOpenItem = new MenuItem { Header = "Открыть gVPN", Style = (Style)FindResource("TrayMenuItemStyle") };
            _trayOpenItem.Click += (s, e) => RestoreFromTray();

            _trayExitItem = new MenuItem
            {
                Header = "Выйти из gVPN",
                Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F38BA8")),
                Style = (Style)FindResource("TrayMenuItemStyle")
            };
            _trayExitItem.Click += (s, e) => { _isRealExit = true; this.Close(); };

            _trayMenu.Items.Add(_trayStatusItem);
            _trayMenu.Items.Add(_traySpeedItem);
            _trayMenu.Items.Add(new Separator { Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#313244")) });
            _trayMenu.Items.Add(_trayConnectItem);
            _trayMenu.Items.Add(_trayOpenItem);
            _trayMenu.Items.Add(new Separator { Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#313244")) });
            _trayMenu.Items.Add(_trayExitItem);

            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) RestoreFromTray();
                else if (e.Button == MouseButtons.Right) _trayMenu.IsOpen = true;
            };
        }

        public void UpdateTrayLocalization(LocalizedStrings s, bool isConnected, string? profileName)
        {
            if (_trayConnectItem != null) _trayConnectItem.Header = isConnected ? s.Disconnect : s.Connect;
            if (_trayStatusItem != null) _trayStatusItem.Header = isConnected ? $"gVPN: {profileName}" : s.TrayStatusDisconnected;
            if (_trayOpenItem != null) _trayOpenItem.Header = s.TrayOpen;
            if (_trayExitItem != null) _trayExitItem.Header = s.TrayExit;
        }

        public void RestoreFromTray()
        {
            ShowMainView();
            this.ShowInTaskbar = true;
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            this.Topmost = true;
            this.Topmost = false;
        }

        private void MinimizeToTray()
        {
            this.WindowState = WindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Hide();
            TrimMemory();
        }

        // ================= НАВИГАЦИЯ ОКНА =================
        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                this.DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => MinimizeToTray();
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
                PulseGuardService.WriteStatus(PulseGuardState.Disconnected, CurrentProfileForStatus);
                _xrayService.Stop();
                _speedTimer.Stop();
                _autoPingTimer.Stop();
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
            BtnSettings.Content = "\uE713";
            TxtHeaderTitle.Text = "gVPN";
        }

        private void ShowSettingsView()
        {
            PnlMainView.Visibility = Visibility.Collapsed;
            PnlSettingsView.Visibility = Visibility.Visible;
            BtnSettings.Content = "\uE72B";
            TxtHeaderTitle.Text = LocalizationService.GetStrings(_settings.Language).SettingsTitle;
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

        private void SaveSettingsFromUi(bool updateAutoStartTask = false)
        {
            if (ChkAutoStartApp == null || RbTunnelApps == null)
                return;

            _settings.AutoStartApp = ChkAutoStartApp.IsChecked == true;
            _settings.AutoStartConnect = ChkAutoStartConnect.IsChecked == true;
            _settings.StartMinimized = ChkStartMinimized.IsChecked == true;
            _settings.TunnelOnlySelectedApps = RbTunnelApps.IsChecked == true;
            _settings.TargetAppPaths = _targetApps.ToList();

            if (LstServers.SelectedItem is VpnProfile p)
                _settings.LastSelectedProfileId = p.Id;

            SettingsService.SaveSettings(_settings, updateAutoStartTask);
        }

        private void OnAutoStartChanged(object sender, RoutedEventArgs e)
        {
            if (!_isLoadingSettings) SaveSettingsFromUi(updateAutoStartTask: true);
        }

        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (!_isLoadingSettings) SaveSettingsFromUi(updateAutoStartTask: false);
        }

        private void OnRoutingModeChanged(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings || RbTunnelApps == null || PnlAppsList == null)
                return;

            bool onlyApps = RbTunnelApps.IsChecked == true;
            PnlAppsList.Visibility = onlyApps ? Visibility.Visible : Visibility.Collapsed;
            SaveSettingsFromUi(updateAutoStartTask: false);
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingSettings) return;
            if (CmbLanguage.SelectedItem is ComboBoxItem item && item.Tag is string lang)
            {
                _settings.Language = lang;
                LocalizationService.UpdateUi(this, lang, _isConnected, _activeProfile?.Name);
                SaveSettingsFromUi(updateAutoStartTask: false);
            }
        }

        private void BtnAddApp_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Приложения (*.exe)|*.exe",
                Title = "Выберите файл .exe"
            };
            if (ofd.ShowDialog() == true && !_targetApps.Contains(ofd.FileName))
            {
                _targetApps.Add(ofd.FileName);
                SaveSettingsFromUi(updateAutoStartTask: false);
            }
        }

        private void BtnRemoveApp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is string path)
            {
                _targetApps.Remove(path);
                SaveSettingsFromUi(updateAutoStartTask: false);
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

        // ================= ПРОФИЛИ СЕРВЕРОВ =================
        private void BtnToggleAddPanel_Click(object sender, RoutedEventArgs e)
        {
            bool show = PnlAddServer.Visibility != Visibility.Visible;
            PnlAddServer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LocalizationService.UpdateUi(this, _settings.Language, _isConnected, _activeProfile?.Name);
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
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    string raw = await client.GetStringAsync(input);
                    string decoded = raw;
                    try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Trim())); } catch { }

                    foreach (var line in decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        ParseAndAddLine(line.Trim());
                    }
                }
                else if (input.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
                {
                    var p = new VpnProfile { Name = $"WireGuard #{_profiles.Count + 1}", Type = "wireguard", RawData = input };
                    _profiles.Add(p);
                    SettingsService.SaveProfiles(_profiles);
                    LstServers.SelectedItem = p;
                }
                else
                {
                    string textToParse = XrayService.TryDecodeSubscriptionBase64(input, out string decodedText)
                        ? decodedText
                        : input;

                    if (textToParse.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) &&
                        !textToParse.Contains("vless://", StringComparison.OrdinalIgnoreCase) &&
                        !textToParse.Contains("trojan://", StringComparison.OrdinalIgnoreCase) &&
                        !textToParse.Contains("ss://", StringComparison.OrdinalIgnoreCase))
                    {
                        var p = new VpnProfile { Name = $"WireGuard #{_profiles.Count + 1}", Type = "wireguard", RawData = textToParse };
                        _profiles.Add(p);
                        SettingsService.SaveProfiles(_profiles);
                        LstServers.SelectedItem = p;
                    }
                    else
                    {
                        foreach (var line in textToParse.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            ParseAndAddLine(line.Trim());
                        }
                    }
                }

                if (_lastAddedProfile != null)
                {
                    // Одна запись на диск и один выбор элемента вместо N
                    SettingsService.SaveProfiles(_profiles);
                    LstServers.SelectedItem = _lastAddedProfile;
                    _lastAddedProfile = null;
                }

                TxtInput.Clear();
                PnlAddServer.Visibility = Visibility.Collapsed;
                LocalizationService.UpdateUi(this, _settings.Language, _isConnected, _activeProfile?.Name);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
            finally { BtnAdd.IsEnabled = true; }
        }

        private void ParseAndAddLine(string line)
        {
            if (line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                AddUriProfile(line, "vless");
            else if (line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                AddUriProfile(line, "trojan");
            else if (line.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
                AddUriProfile(line, "shadowsocks");
        }

        private void AddUriProfile(string url, string type)
        {
            string name = $"{type.ToUpper()} #{_profiles.Count + 1}";
            try
            {
                var uri = new Uri(url);
                name = !string.IsNullOrEmpty(uri.Fragment)
                    ? HttpUtility.UrlDecode(uri.Fragment.TrimStart('#'))
                    : $"{uri.Host}:{uri.Port}";
            }
            catch { }

            var p = new VpnProfile { Name = name, Type = type, RawData = url };
            _profiles.Add(p);
            _lastAddedProfile = p;
        }

        private async void BtnPingAll_Click(object sender, RoutedEventArgs e)
        {
            await RunPingAllAsync();
        }

        private async void AutoPingTimer_Tick(object? sender, EventArgs e)
        {
            // Не пингуем, пока окно скрыто в трей
            if (!this.IsVisible || this.WindowState == WindowState.Minimized) return;
            await RunPingAllAsync();
        }

        private async Task RunPingAllAsync()
        {
            if (_isPinging || _profiles.Count == 0) return;
            _isPinging = true;

            BtnPingAll.IsEnabled = false;
            var loc = LocalizationService.GetStrings(_settings.Language);
            using var semaphore = new System.Threading.SemaphoreSlim(5);

            try
            {
                var tasks = _profiles.ToList().Select(async profile =>
                {
                    profile.LatencyColor = "#A6ADC8";
                    profile.LatencyText = "...";

                    await semaphore.WaitAsync();
                    try
                    {
                        long? ms = await XrayService.MeasureProfileLatencyAsync(profile);
                        if (ms.HasValue)
                        {
                            profile.LatencyText = $"{ms.Value} ms";
                            profile.LatencyColor = ms.Value < 150 ? "#A6E3A1" : (ms.Value < 350 ? "#F9E2AF" : "#F38BA8");
                        }
                        else
                        {
                            profile.LatencyText = loc.PingTimeout;
                            profile.LatencyColor = "#F38BA8";
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }
            catch { }
            finally
            {
                BtnPingAll.IsEnabled = true;
                _isPinging = false;
            }
        }

        private void BtnRenameServer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is VpnProfile p)
            {
                _renamingProfile = p;
                TxtRename.Text = p.Name;
                PnlRenameServer.Visibility = Visibility.Visible;
                TxtRename.Focus();
                TxtRename.SelectAll();
            }
        }

        private void BtnSaveRename_Click(object sender, RoutedEventArgs e)
        {
            if (_renamingProfile != null)
            {
                string newName = TxtRename.Text.Trim();
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    _renamingProfile.Name = newName;
                    SettingsService.SaveProfiles(_profiles);
                    if (_isConnected && _activeProfile?.Id == _renamingProfile.Id)
                    {
                        LocalizationService.UpdateUi(this, _settings.Language, true, newName);
                        PulseGuardService.WriteStatus(PulseGuardState.Connected, _activeProfile);
                    }
                    else if (!_isConnected && LstServers.SelectedItem is VpnProfile sel && sel.Id == _renamingProfile.Id)
                    {
                        PulseGuardService.WriteStatus(PulseGuardState.Disconnected, sel);
                    }
                }
            }
            _renamingProfile = null;
            PnlRenameServer.Visibility = Visibility.Collapsed;
        }

        private void BtnCancelRename_Click(object sender, RoutedEventArgs e)
        {
            _renamingProfile = null;
            PnlRenameServer.Visibility = Visibility.Collapsed;
        }

        private void TxtRename_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                BtnSaveRename_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                BtnCancelRename_Click(sender, e);
                e.Handled = true;
            }
        }

        private void BtnDeleteServer_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected) return;
            if (sender is FrameworkElement el && el.DataContext is VpnProfile p)
            {
                if (_renamingProfile?.Id == p.Id)
                {
                    _renamingProfile = null;
                    PnlRenameServer.Visibility = Visibility.Collapsed;
                }
                _profiles.Remove(p);
                SettingsService.SaveProfiles(_profiles);
                if (_profiles.Count > 0) LstServers.SelectedIndex = 0;
            }
        }

        private void LstServers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isConnected || _isLoadingSettings) return;

            if (LstServers.SelectedItem is VpnProfile p)
            {
                var s = LocalizationService.GetStrings(_settings.Language);
                TxtStatus.Text = string.Format(s.StatusSelectedFormat, p.Name, p.Type.ToUpper());
                _settings.LastSelectedProfileId = p.Id;
                SaveSettingsFromUi(updateAutoStartTask: false);
                PulseGuardService.WriteStatus(PulseGuardState.Disconnected, p);
            }
        }

        // ================= ПОДКЛЮЧЕНИЕ =================
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.GetStrings(_settings.Language);
            if (!_isConnected)
            {
                if (LstServers.SelectedItem is not VpnProfile p) return;

                _activeProfile = p;
                BtnConnect.IsEnabled = false;
                LstServers.IsEnabled = false;
                TxtStatus.Text = loc.StatusConnecting;
                TxtStatus.Foreground = System.Windows.Media.Brushes.Orange;
                StatusDot.Background = System.Windows.Media.Brushes.Orange;

                try
                {
                    _xrayService.Start(p, !_settings.TunnelOnlySelectedApps, _targetApps);
                    await Task.Delay(800);

                    if (!_xrayService.IsRunning)
                    {
                        string log = _xrayService.GetLastErrorLog();
                        string msg = string.IsNullOrWhiteSpace(log)
                            ? loc.XrayExitedError
                            : $"{loc.XrayExitedError}\n\n{log}";
                        throw new Exception(msg);
                    }

                    bool reachable = await _xrayService.VerifyConnectionAsync();
                    if (!reachable)
                        throw new Exception(loc.ServerTimeoutError);

                    _isConnected = true;
                    BtnConnect.Content = loc.Disconnect;
                    BtnConnect.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F38BA8"));
                    TxtStatus.Text = string.Format(loc.StatusConnectedFormat, p.Name);
                    TxtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                    StatusDot.Background = System.Windows.Media.Brushes.LightGreen;

                    UpdateTrayLocalization(loc, true, p.Name);
                    if (_traySpeedItem != null) _traySpeedItem.Visibility = Visibility.Visible;

                    _cachedInterfaces = NetworkInterface.GetAllNetworkInterfaces();
                    _lastBytesReceived = 0;
                    _lastBytesSent = 0;
                    PnlSpeed.Visibility = Visibility.Visible;
                    _speedTimer.Start();
                    PulseGuardService.WriteStatus(PulseGuardState.Connected, p);
                    PulseGuardService.RefreshExternalIpAsync(p, () => _isConnected && _activeProfile?.Id == p.Id);
                    TrimMemory();
                }
                catch (Exception ex)
                {
                    _xrayService.Stop();
                    _activeProfile = null;
                    LstServers.IsEnabled = true;
                    TxtStatus.Text = loc.StatusError;
                    TxtStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                    StatusDot.Background = System.Windows.Media.Brushes.IndianRed;
                    PulseGuardService.WriteStatus(PulseGuardState.Error, p);
                    MessageBox.Show(ex.Message, loc.VpnErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    BtnConnect.IsEnabled = true;
                }
            }
            else
            {
                _xrayService.Stop();
                _speedTimer.Stop();
                _isConnected = false;
                _activeProfile = null;
                LstServers.IsEnabled = true;
                PnlSpeed.Visibility = Visibility.Collapsed;

                BtnConnect.Content = loc.Connect;
                BtnConnect.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#89B4FA"));
                TxtStatus.Text = loc.StatusDisconnected;
                TxtStatus.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#BAC2DE"));
                StatusDot.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#89B4FA"));

                UpdateTrayLocalization(loc, false, null);
                if (_traySpeedItem != null) _traySpeedItem.Visibility = Visibility.Collapsed;
                PulseGuardService.WriteStatus(PulseGuardState.Disconnected, LstServers.SelectedItem as VpnProfile);
                TrimMemory();
            }
        }

        private void HandleConnectionDropped()
        {
            var droppedProfile = _activeProfile;
            var loc = LocalizationService.GetStrings(_settings.Language);

            _xrayService.Stop();
            _speedTimer.Stop();
            _isConnected = false;
            _activeProfile = null;
            LstServers.IsEnabled = true;
            PnlSpeed.Visibility = Visibility.Collapsed;

            BtnConnect.Content = loc.Connect;
            BtnConnect.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#89B4FA"));
            TxtStatus.Text = loc.StatusError;
            TxtStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
            StatusDot.Background = System.Windows.Media.Brushes.IndianRed;

            UpdateTrayLocalization(loc, false, null);
            if (_traySpeedItem != null) _traySpeedItem.Visibility = Visibility.Collapsed;
            PulseGuardService.WriteStatus(PulseGuardState.Error, droppedProfile);
            TrimMemory();
        }

        private void SpeedTimer_Tick(object? sender, EventArgs e)
        {
            if (_isConnected && !_xrayService.IsRunning)
            {
                HandleConnectionDropped();
                return;
            }

            if (_cachedInterfaces == null) return;
            long r = 0, s = 0;
            foreach (var ni in _cachedInterfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
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
                TxtDownloadSpeed.Text = $"↓ {d:F2} MB/s";
                TxtUploadSpeed.Text = $"↑ {u:F2} MB/s";
                if (_traySpeedItem != null && _traySpeedItem.Visibility == Visibility.Visible)
                    _traySpeedItem.Header = $"↓ {d:F1} MB/s  ↑ {u:F1} MB/s";
            }

            _lastBytesReceived = r;
            _lastBytesSent = s;
        }
    }
}