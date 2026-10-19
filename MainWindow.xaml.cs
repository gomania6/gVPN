using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

using Application = System.Windows.Application;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace gVPN
{
    public class VpnProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Новый сервер";
        public string Type { get; set; } = "vless";
        public string RawData { get; set; } = "";
    }

    public class AppSettings
    {
        public string Language { get; set; } = "ru"; // ru, en, pl
        public bool AutoStartApp { get; set; } = false;
        public bool AutoStartConnect { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public string? LastSelectedProfileId { get; set; }
    }

    [SupportedOSPlatform("windows")]
    public partial class MainWindow : Window
    {
        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gVPN");
        private readonly string _storagePath = Path.Combine(AppDataFolder, "servers.json");
        private readonly string _settingsPath = Path.Combine(AppDataFolder, "settings.json");
        private readonly string _coreDir = Path.Combine(AppDataFolder, "Core");

        private ObservableCollection<VpnProfile> _profiles = new();
        private AppSettings _settings = new();
        private bool _isConnected = false;
        private Process? _xrayProcess;
        private VpnProfile? _activeProfile;
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
        private MenuItem? _trayShowItem;
        private MenuItem? _trayExitItem;

        private static readonly HttpClient SharedHttpClient = new(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        [DllImport("wininet.dll")]
        public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

        public MainWindow()
        {
            // Фикс рабочей директории при автозапуске Windows
            string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(exeDir))
            {
                Directory.SetCurrentDirectory(exeDir);
            }

            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            InitializeComponent();
            EnsureCoreExtracted();
            SetupTrayIcon();
            LoadProfiles();
            LoadSettings();

            LstServers.ItemsSource = _profiles;

            if (!string.IsNullOrEmpty(_settings.LastSelectedProfileId))
            {
                var saved = _profiles.FirstOrDefault(p => p.Id == _settings.LastSelectedProfileId);
                if (saved != null) LstServers.SelectedItem = saved;
                else if (_profiles.Count > 0) LstServers.SelectedIndex = 0;
            }
            else if (_profiles.Count > 0)
            {
                LstServers.SelectedIndex = 0;
            }

            _speedTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _speedTimer.Tick += SpeedTimer_Tick;

            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TrimMemory();

            // Проверка запуска с аргументом фонового старта или флагом настроек
            string[] args = Environment.GetCommandLineArgs();
            bool hasAutoStartFlag = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

            if (_settings.StartMinimized || hasAutoStartFlag)
            {
                this.Hide();
            }

            if (_settings.AutoStartConnect && LstServers.SelectedItem != null)
            {
                await Task.Delay(800);
                BtnConnect_Click(BtnConnect, new RoutedEventArgs());
            }
        }

        // ================= ЛОКАЛИЗАЦИЯ (МУЛЬТИЯЗЫЧНОСТЬ) =================

        private void ApplyLocalization(string lang)
        {
            switch (lang)
            {
                case "en":
                    LblAvailableServers.Text = "Available Servers";
                    BtnToggleAddPanel.Content = PnlAddServer.Visibility == Visibility.Visible ? "Hide Form" : "+ Add";
                    LblAddHint.Text = "Paste vless://, subscription URL, or WG config:";
                    BtnAdd.Content = "Save Connection";
                    BtnConnect.Content = _isConnected ? "Disconnect" : "Connect";
                    TxtStatus.Text = _isConnected ? $"Connected: {_activeProfile?.Name}" : "Status: Disconnected";
                    LblGeneralSettings.Text = "General Settings";
                    LblLanguage.Text = "Interface Language";
                    ChkAutoStartApp.Content = "Start gVPN with Windows";
                    ChkAutoStartConnect.Content = "Automatically connect selected server";
                    ChkStartMinimized.Content = "Start minimized to system tray";
                    BtnBackToServers.Content = "← Back to Servers";
                    if (_trayStatusItem != null) _trayStatusItem.Header = _isConnected ? $"Connected: {_activeProfile?.Name}" : "gVPN: Disconnected";
                    if (_trayConnectItem != null) _trayConnectItem.Header = _isConnected ? "Disconnect" : "Connect";
                    if (_trayShowItem != null) _trayShowItem.Header = "Open Window";
                    if (_trayExitItem != null) _trayExitItem.Header = "Exit gVPN";
                    break;

                case "pl":
                    LblAvailableServers.Text = "Dostępne serwery";
                    BtnToggleAddPanel.Content = PnlAddServer.Visibility == Visibility.Visible ? "Ukryj" : "+ Dodaj";
                    LblAddHint.Text = "Wklej link vless://, subskrypcję lub plik .conf WireGuard:";
                    BtnAdd.Content = "Zapisz połączenie";
                    BtnConnect.Content = _isConnected ? "Rozłącz" : "Połącz";
                    TxtStatus.Text = _isConnected ? $"Połączono: {_activeProfile?.Name}" : "Status: Rozłączono";
                    LblGeneralSettings.Text = "Ustawienia ogólne";
                    LblLanguage.Text = "Język interfejsu";
                    ChkAutoStartApp.Content = "Uruchom gVPN wraz z systemem Windows";
                    ChkAutoStartConnect.Content = "Automatycznie połącz wybrany serwer";
                    ChkStartMinimized.Content = "Uruchom zminimalizowany w zasobniku";
                    BtnBackToServers.Content = "← Wróć do serwerów";
                    if (_trayStatusItem != null) _trayStatusItem.Header = _isConnected ? $"Połączono: {_activeProfile?.Name}" : "gVPN: Rozłączono";
                    if (_trayConnectItem != null) _trayConnectItem.Header = _isConnected ? "Rozłącz" : "Połącz";
                    if (_trayShowItem != null) _trayShowItem.Header = "Otwórz okno";
                    if (_trayExitItem != null) _trayExitItem.Header = "Zamknij gVPN";
                    break;

                default: // ru
                    LblAvailableServers.Text = "Доступные серверы";
                    BtnToggleAddPanel.Content = PnlAddServer.Visibility == Visibility.Visible ? "Скрыть форму" : "+ Добавить";
                    LblAddHint.Text = "Вставьте vless://, URL подписки или WG конфиг:";
                    BtnAdd.Content = "Сохранить подключение";
                    BtnConnect.Content = _isConnected ? "Отключиться" : "Подключиться";
                    TxtStatus.Text = _isConnected ? $"Подключено: {_activeProfile?.Name}" : "Статус: Отключено";
                    LblGeneralSettings.Text = "Общие параметры";
                    LblLanguage.Text = "Язык интерфейса";
                    ChkAutoStartApp.Content = "Запускать gVPN вместе с Windows";
                    ChkAutoStartConnect.Content = "Автоматически подключать выбранный сервер";
                    ChkStartMinimized.Content = "Запускать свернутым в системный трей";
                    BtnBackToServers.Content = "← Назад к серверам";
                    if (_trayStatusItem != null) _trayStatusItem.Header = _isConnected ? $"Подключено: {_activeProfile?.Name}" : "gVPN: Отключено";
                    if (_trayConnectItem != null) _trayConnectItem.Header = _isConnected ? "Отключиться" : "Подключиться";
                    if (_trayShowItem != null) _trayShowItem.Header = "Открыть окно";
                    if (_trayExitItem != null) _trayExitItem.Header = "Выйти из gVPN";
                    break;
            }
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingSettings) return;

            if (CmbLanguage.SelectedItem is ComboBoxItem item && item.Tag is string lang)
            {
                _settings.Language = lang;
                ApplyLocalization(lang);
                SaveSettings();
            }
        }

        // ================= НАВИГАЦИЯ МЕЖДУ ЭКРАНАМИ =================

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (PnlSettingsView.Visibility == Visibility.Visible)
                ShowMainView();
            else
                ShowSettingsView();
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
            TxtHeaderTitle.Text = _settings.Language switch
            {
                "en" => "Settings",
                "pl" => "Ustawienia",
                _ => "Настройки"
            };
        }

        // ================= НАДЕЖНЫЙ АВТОЗАПУСК WINDOWS =================

        private void LoadSettings()
        {
            _isLoadingSettings = true;
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (s != null) _settings = s;
                }
            }
            catch { }

            ChkAutoStartApp.IsChecked = _settings.AutoStartApp;
            ChkAutoStartConnect.IsChecked = _settings.AutoStartConnect;
            ChkStartMinimized.IsChecked = _settings.StartMinimized;

            foreach (ComboBoxItem item in CmbLanguage.Items)
            {
                if (item.Tag?.ToString() == _settings.Language)
                {
                    CmbLanguage.SelectedItem = item;
                    break;
                }
            }

            ApplyLocalization(_settings.Language);
            _isLoadingSettings = false;
        }

        private void SaveSettings()
        {
            try
            {
                if (!Directory.Exists(AppDataFolder))
                    Directory.CreateDirectory(AppDataFolder);

                _settings.AutoStartApp = ChkAutoStartApp.IsChecked == true;
                _settings.AutoStartConnect = ChkAutoStartConnect.IsChecked == true;
                _settings.StartMinimized = ChkStartMinimized.IsChecked == true;

                if (LstServers.SelectedItem is VpnProfile profile)
                    _settings.LastSelectedProfileId = profile.Id;

                string json = JsonSerializer.Serialize(_settings, JsonOptions);
                File.WriteAllText(_settingsPath, json);

                ApplyWindowsAutoStart(_settings.AutoStartApp);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сохранения настроек: {ex.Message}");
            }
        }

        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (!_isLoadingSettings)
                SaveSettings();
        }

        private void ApplyWindowsAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;

                // Для .NET 8 Single-File используем строгий ProcessPath
                string? appPath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(appPath)) return;

                if (enable)
                {
                    // Ключ автозапуска с параметром --autostart
                    key.SetValue("gVPN", $"\"{appPath}\" --autostart");
                }
                else
                {
                    if (key.GetValue("gVPN") != null)
                        key.DeleteValue("gVPN", false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка настройки автозапуска Windows: {ex.Message}");
            }
        }

        // ================= ОПТИМИЗАЦИЯ ПАМЯТИ =================

        private static void TrimMemory()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
                }
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
                Text = "gVPN — VPN Client",
                Icon = appIcon ?? System.Drawing.SystemIcons.Application
            };

            _trayMenu = new ContextMenu { Style = (Style)FindResource("TrayMenuStyle") };

            _trayStatusItem = new MenuItem
            {
                Header = "gVPN: Отключено",
                IsEnabled = false,
                Style = (Style)FindResource("TrayMenuItemStyle")
            };

            _traySpeedItem = new MenuItem
            {
                Header = "Скорость: —",
                IsEnabled = false,
                Visibility = Visibility.Collapsed,
                Style = (Style)FindResource("TrayMenuItemStyle")
            };

            _trayConnectItem = new MenuItem
            {
                Header = "Подключиться",
                Style = (Style)FindResource("TrayMenuItemStyle")
            };
            _trayConnectItem.Click += (s, e) => BtnConnect_Click(BtnConnect, new RoutedEventArgs());

            _trayShowItem = new MenuItem
            {
                Header = "Открыть окно",
                Style = (Style)FindResource("TrayMenuItemStyle")
            };
            _trayShowItem.Click += (s, e) => RestoreFromTray();

            _trayExitItem = new MenuItem
            {
                Header = "Выйти из gVPN",
                Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F38BA8")),
                Style = (Style)FindResource("TrayMenuItemStyle")
            };
            _trayExitItem.Click += (s, e) =>
            {
                _isRealExit = true;
                this.Close();
            };

            _trayMenu.Items.Add(_trayStatusItem);
            _trayMenu.Items.Add(_traySpeedItem);
            _trayMenu.Items.Add(new System.Windows.Controls.Separator { Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#313244")) });
            _trayMenu.Items.Add(_trayConnectItem);
            _trayMenu.Items.Add(_trayShowItem);
            _trayMenu.Items.Add(new System.Windows.Controls.Separator { Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#313244")) });
            _trayMenu.Items.Add(_trayExitItem);

            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    RestoreFromTray();
                else if (e.Button == MouseButtons.Right)
                    _trayMenu.IsOpen = true;
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

        // ================= ИЗВЛЕЧЕНИЕ ЯДРА =================

        private void EnsureCoreExtracted()
        {
            if (!Directory.Exists(_coreDir))
                Directory.CreateDirectory(_coreDir);

            ExtractEmbeddedBinary("xray.exe", Path.Combine(_coreDir, "xray.exe"));
        }

        private void ExtractEmbeddedBinary(string fileName, string destinationPath)
        {
            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
                return;

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            string? targetResource = null;

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (resourceName.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    targetResource = resourceName;
                    break;
                }
            }

            if (targetResource == null)
            {
                MessageBox.Show($"Ресурс {fileName} не найден!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            using Stream? stream = assembly.GetManifestResourceStream(targetResource);
            if (stream != null)
            {
                using FileStream fileStream = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.CopyTo(fileStream);
            }
        }

        // ================= УПРАВЛЕНИЕ ОКНОМ =================

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                this.DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
            TrimMemory();
        }

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
                DisconnectAll();
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
            }
        }

        private void BtnToggleAddPanel_Click(object sender, RoutedEventArgs e)
        {
            if (PnlAddServer.Visibility == Visibility.Visible)
            {
                PnlAddServer.Visibility = Visibility.Collapsed;
                BtnToggleAddPanel.Content = _settings.Language switch
                {
                    "en" => "+ Add",
                    "pl" => "+ Dodaj",
                    _ => "+ Добавить"
                };
            }
            else
            {
                PnlAddServer.Visibility = Visibility.Visible;
                BtnToggleAddPanel.Content = _settings.Language switch
                {
                    "en" => "Hide Form",
                    "pl" => "Ukryj",
                    _ => "Скрыть форму"
                };
                TxtInput.Focus();
            }
        }

        // ================= ПРОФИЛИ =================

        private void LoadProfiles()
        {
            try
            {
                if (File.Exists(_storagePath))
                {
                    string json = File.ReadAllText(_storagePath);
                    var list = JsonSerializer.Deserialize<List<VpnProfile>>(json, JsonOptions);
                    if (list != null)
                        _profiles = new ObservableCollection<VpnProfile>(list);
                }
            }
            catch { }
        }

        private void SaveProfiles()
        {
            try
            {
                if (!Directory.Exists(AppDataFolder))
                    Directory.CreateDirectory(AppDataFolder);

                string json = JsonSerializer.Serialize(_profiles, JsonOptions);
                File.WriteAllText(_storagePath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сохранения: {ex.Message}");
            }
        }

        private async void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            string input = TxtInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(input)) return;

            BtnAdd.IsEnabled = false;
            try
            {
                if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    string raw = await SharedHttpClient.GetStringAsync(input);
                    string decoded = raw;
                    try
                    {
                        byte[] data = Convert.FromBase64String(raw.Trim());
                        decoded = Encoding.UTF8.GetString(data);
                    }
                    catch { }

                    var lines = decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    int added = 0;
                    foreach (var line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                        {
                            AddVlessProfile(trimmed);
                            added++;
                        }
                    }

                    if (added == 0 && decoded.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                    {
                        AddVlessProfile(decoded.Trim());
                        added++;
                    }

                    MessageBox.Show($"Добавлено серверов: {added}", "Импорт", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (input.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                {
                    AddVlessProfile(input);
                }
                else if (input.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
                {
                    var profile = new VpnProfile
                    {
                        Name = $"WireGuard #{_profiles.Count + 1}",
                        Type = "wireguard",
                        RawData = input
                    };
                    _profiles.Add(profile);
                    SaveProfiles();
                    LstServers.SelectedItem = profile;
                }

                TxtInput.Clear();
                PnlAddServer.Visibility = Visibility.Collapsed;
                BtnToggleAddPanel.Content = _settings.Language switch
                {
                    "en" => "+ Add",
                    "pl" => "+ Dodaj",
                    _ => "+ Добавить"
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}");
            }
            finally
            {
                BtnAdd.IsEnabled = true;
            }
        }

        private void AddVlessProfile(string vlessUrl)
        {
            string serverName = $"VLESS #{_profiles.Count + 1}";
            try
            {
                var uri = new Uri(vlessUrl);
                if (!string.IsNullOrEmpty(uri.Fragment))
                    serverName = HttpUtility.UrlDecode(uri.Fragment.TrimStart('#'));
                else
                    serverName = $"{uri.Host}:{uri.Port}";
            }
            catch { }

            var profile = new VpnProfile
            {
                Name = serverName,
                Type = "vless",
                RawData = vlessUrl
            };
            _profiles.Add(profile);
            SaveProfiles();
            LstServers.SelectedItem = profile;
        }

        private void BtnDeleteServer_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected) return;

            if (sender is FrameworkElement element && element.DataContext is VpnProfile profile)
            {
                _profiles.Remove(profile);
                SaveProfiles();
                if (_profiles.Count > 0)
                    LstServers.SelectedIndex = 0;
            }
        }

        private void LstServers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isConnected && _activeProfile != null && LstServers.SelectedItem != _activeProfile)
            {
                LstServers.SelectedItem = _activeProfile;
                return;
            }

            if (LstServers.SelectedItem is VpnProfile profile)
            {
                string selPrefix = _settings.Language switch
                {
                    "en" => "Selected",
                    "pl" => "Wybrano",
                    _ => "Выбран"
                };
                TxtStatus.Text = $"{selPrefix}: {profile.Name} ({profile.Type.ToUpper()})";
                _settings.LastSelectedProfileId = profile.Id;
                SaveSettings();
            }
        }

        // ================= ПОДКЛЮЧЕНИЕ =================

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                if (LstServers.SelectedItem is not VpnProfile profile) return;

                _activeProfile = profile;
                BtnConnect.IsEnabled = false;
                TxtStatus.Text = _settings.Language switch
                {
                    "en" => "Connecting...",
                    "pl" => "Łączenie...",
                    _ => "Подключение..."
                };
                TxtStatus.Foreground = System.Windows.Media.Brushes.Orange;

                try
                {
                    if (profile.Type == "vless")
                        StartXray(profile.RawData);
                    else
                        StartWireGuard(profile.RawData);

                    bool isOnline = await VerifyConnectionAsync();

                    if (!isOnline)
                    {
                        DisconnectAll();
                        TxtStatus.Text = _settings.Language switch
                        {
                            "en" => "Error: Server not responding",
                            "pl" => "Błąd: Serwer nie odpowiada",
                            _ => "Ошибка: сервер не отвечает"
                        };
                        TxtStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                        return;
                    }

                    _isConnected = true;
                    BtnConnect.Content = _settings.Language switch
                    {
                        "en" => "Disconnect",
                        "pl" => "Rozłącz",
                        _ => "Отключиться"
                    };
                    BtnConnect.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F38BA8"));

                    string connPrefix = _settings.Language switch
                    {
                        "en" => "Connected",
                        "pl" => "Połączono",
                        _ => "Подключено"
                    };
                    TxtStatus.Text = $"{connPrefix}: {profile.Name}";
                    TxtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;

                    if (_trayConnectItem != null) _trayConnectItem.Header = BtnConnect.Content;
                    if (_trayStatusItem != null) _trayStatusItem.Header = $"{connPrefix}: {profile.Name}";
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
                    DisconnectAll();
                    TxtStatus.Text = "Error";
                    TxtStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                    MessageBox.Show(ex.Message);
                }
                finally
                {
                    BtnConnect.IsEnabled = true;
                }
            }
            else
            {
                DisconnectAll();
                _isConnected = false;
                _activeProfile = null;
                BtnConnect.Content = _settings.Language switch
                {
                    "en" => "Connect",
                    "pl" => "Połącz",
                    _ => "Подключиться"
                };
                BtnConnect.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#89B4FA"));
                TxtStatus.Text = _settings.Language switch
                {
                    "en" => "Status: Disconnected",
                    "pl" => "Status: Rozłączono",
                    _ => "Статус: Отключено"
                };
                TxtStatus.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#BAC2DE"));

                if (_trayConnectItem != null) _trayConnectItem.Header = BtnConnect.Content;
                if (_trayStatusItem != null) _trayStatusItem.Header = _settings.Language switch
                {
                    "en" => "gVPN: Disconnected",
                    "pl" => "gVPN: Rozłączono",
                    _ => "gVPN: Отключено"
                };
                if (_traySpeedItem != null) _traySpeedItem.Visibility = Visibility.Collapsed;
                TrimMemory();
            }
        }

        private async Task<bool> VerifyConnectionAsync()
        {
            await Task.Delay(1200);
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var handler = new HttpClientHandler
                    {
                        Proxy = new WebProxy("http://127.0.0.1:10809"),
                        UseProxy = true
                    };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                    var response = await client.GetAsync("http://cp.cloudflare.com/generate_204");
                    if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent)
                        return true;
                }
                catch { await Task.Delay(800); }
            }
            return false;
        }

        private void SpeedTimer_Tick(object? sender, EventArgs e)
        {
            if (_cachedInterfaces == null) return;
            long currentReceived = 0, currentSent = 0;

            foreach (var ni in _cachedInterfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    try
                    {
                        var stats = ni.GetIPStatistics();
                        currentReceived += stats.BytesReceived;
                        currentSent += stats.BytesSent;
                    }
                    catch { }
                }
            }

            if (_lastBytesReceived > 0 && _lastBytesSent > 0)
            {
                double downSpeedMb = Math.Max(0, currentReceived - _lastBytesReceived) / 1048576.0;
                double upSpeedMb = Math.Max(0, currentSent - _lastBytesSent) / 1048576.0;

                string mbUnit = _settings.Language == "en" ? "MB/s" : "МБ/с";
                TxtDownloadSpeed.Text = $"↓ {downSpeedMb:F2} {mbUnit}";
                TxtUploadSpeed.Text = $"↑ {upSpeedMb:F2} {mbUnit}";

                if (_traySpeedItem != null && _traySpeedItem.Visibility == Visibility.Visible)
                {
                    string spdPrefix = _settings.Language switch { "en" => "Speed", "pl" => "Prędkość", _ => "Скорость" };
                    _traySpeedItem.Header = $"{spdPrefix}: ↓ {downSpeedMb:F1} {mbUnit} | ↑ {upSpeedMb:F1} {mbUnit}";
                }
            }

            _lastBytesReceived = currentReceived;
            _lastBytesSent = currentSent;
        }

        // ================= VLESS & WIREGUARD =================

        private void StartXray(string vlessUrl)
        {
            string configJson = GenerateXrayConfig(vlessUrl);
            string configPath = Path.Combine(AppDataFolder, "config.json");
            File.WriteAllText(configPath, configJson);

            string xrayExe = Path.Combine(_coreDir, "xray.exe");
            if (!File.Exists(xrayExe)) throw new FileNotFoundException("xray.exe not found");

            ProcessStartInfo psi = new()
            {
                FileName = xrayExe,
                Arguments = $"run -config \"{configPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            psi.EnvironmentVariables["GOMEMLIMIT"] = "24MiB";
            psi.EnvironmentVariables["GOGC"] = "30";

            _xrayProcess = Process.Start(psi);
            SetSystemProxy(true, "127.0.0.1:10809");
        }

        private string GenerateXrayConfig(string vlessUrl)
        {
            var uri = new Uri(vlessUrl);
            string uuid = uri.UserInfo;
            string host = uri.Host;
            int port = uri.Port;
            var query = HttpUtility.ParseQueryString(uri.Query);

            string sni = query["sni"] ?? "dl.google.com";
            string pbk = query["pbk"] ?? "";
            string sid = query["sid"] ?? "";
            string fp = query["fp"] ?? "chrome";
            string spx = query["spx"] ?? "";
            string flow = query["flow"] ?? "xtls-rprx-vision";

            return $$"""
            {
              "log": { "loglevel": "none" },
              "inbounds": [
                {
                  "port": 10808,
                  "listen": "127.0.0.1",
                  "protocol": "socks",
                  "settings": { "auth": "noauth", "udp": true }
                },
                {
                  "port": 10809,
                  "listen": "127.0.0.1",
                  "protocol": "http"
                }
              ],
              "outbounds": [
                {
                  "protocol": "vless",
                  "settings": {
                    "vnext": [{
                      "address": "{{host}}",
                      "port": {{port}},
                      "users": [{
                        "id": "{{uuid}}",
                        "encryption": "none",
                        "flow": "{{flow}}"
                      }]
                    }]
                  },
                  "streamSettings": {
                    "network": "tcp",
                    "security": "reality",
                    "realitySettings": {
                      "show": false,
                      "fingerprint": "{{fp}}",
                      "serverName": "{{sni}}",
                      "publicKey": "{{pbk}}",
                      "shortId": "{{sid}}",
                      "spiderX": "{{spx}}"
                    }
                  }
                }
              ]
            }
            """;
        }

        private void StartWireGuard(string confContent)
        {
            string configJson = GenerateWireGuardXrayConfig(confContent);
            string configPath = Path.Combine(AppDataFolder, "config.json");
            File.WriteAllText(configPath, configJson);

            string xrayExe = Path.Combine(_coreDir, "xray.exe");
            if (!File.Exists(xrayExe)) throw new FileNotFoundException("xray.exe not found");

            ProcessStartInfo psi = new()
            {
                FileName = xrayExe,
                Arguments = $"run -config \"{configPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            psi.EnvironmentVariables["GOMEMLIMIT"] = "24MiB";
            psi.EnvironmentVariables["GOGC"] = "30";

            _xrayProcess = Process.Start(psi);
            SetSystemProxy(true, "127.0.0.1:10809");
        }

        private string GenerateWireGuardXrayConfig(string confContent)
        {
            string privateKey = "", address = "10.0.0.2/32", endpoint = "", publicKey = "", presharedKey = "";
            int mtu = 1420;

            var lines = confContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("#") || !line.Contains('=')) continue;

                int eqIndex = line.IndexOf('=');
                string key = line.Substring(0, eqIndex).Trim();
                string val = line.Substring(eqIndex + 1).Trim();

                if (key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase)) privateKey = val;
                else if (key.Equals("Address", StringComparison.OrdinalIgnoreCase)) address = val.Contains('/') ? val : $"{val}/32";
                else if (key.Equals("PublicKey", StringComparison.OrdinalIgnoreCase)) publicKey = val;
                else if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase)) endpoint = val;
                else if (key.Equals("PresharedKey", StringComparison.OrdinalIgnoreCase)) presharedKey = val;
                else if (key.Equals("MTU", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int parsedMtu)) mtu = parsedMtu;
            }

            int lastColon = endpoint.LastIndexOf(':');
            string epHost = lastColon > 0 ? endpoint.Substring(0, lastColon).Trim() : endpoint;
            int epPort = lastColon > 0 && int.TryParse(endpoint.Substring(lastColon + 1).Trim(), out int p) ? p : 51820;

            string pskEntry = string.IsNullOrWhiteSpace(presharedKey) ? "" : $"\"preSharedKey\": \"{presharedKey}\",";

            return $$"""
            {
              "log": { "loglevel": "warning" },
              "inbounds": [
                {
                  "tag": "socks-in",
                  "port": 10808,
                  "listen": "127.0.0.1",
                  "protocol": "socks",
                  "settings": { "auth": "noauth", "udp": true }
                },
                {
                  "tag": "http-in",
                  "port": 10809,
                  "listen": "127.0.0.1",
                  "protocol": "http"
                }
              ],
              "outbounds": [
                {
                  "protocol": "wireguard",
                  "settings": {
                    "secretKey": "{{privateKey}}",
                    "address": ["{{address}}"],
                    "peers": [
                      {
                        "publicKey": "{{publicKey}}",
                        {{pskEntry}}
                        "endpoint": "{{epHost}}:{{epPort}}"
                      }
                    ],
                    "mtu": {{mtu}}
                  }
                }
              ]
            }
            """;
        }

        private void DisconnectAll()
        {
            _speedTimer.Stop();
            PnlSpeed.Visibility = Visibility.Collapsed;
            _cachedInterfaces = null;

            if (_xrayProcess != null && !_xrayProcess.HasExited)
            {
                try { _xrayProcess.Kill(); } catch { }
                _xrayProcess.Dispose();
                _xrayProcess = null;
            }

            SetSystemProxy(false);
        }

        private void SetSystemProxy(bool enable, string proxyServer = "")
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (key != null)
            {
                key.SetValue("ProxyEnable", enable ? 1 : 0);
                if (enable) key.SetValue("ProxyServer", proxyServer);

                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
        }
    }
}