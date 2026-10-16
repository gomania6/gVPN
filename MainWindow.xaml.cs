using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;

namespace gVPN
{
    public class VpnProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Новый сервер";
        public string Type { get; set; } = "vless"; // "vless" или "wireguard"
        public string RawData { get; set; } = "";
    }

    [SupportedOSPlatform("windows")]
    public partial class MainWindow : Window
    {
        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gVPN");
        private readonly string _storagePath = Path.Combine(AppDataFolder, "servers.json");
        private readonly string _coreDir = Path.Combine(AppDataFolder, "Core");

        private ObservableCollection<VpnProfile> _profiles = new();
        private bool _isConnected = false;
        private Process? _xrayProcess;
        private VpnProfile? _activeProfile;

        private readonly DispatcherTimer _speedTimer;
        private NetworkInterface[]? _cachedInterfaces;
        private long _lastBytesReceived = 0;
        private long _lastBytesSent = 0;

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
            InitializeComponent();
            EnsureCoreExtracted();
            LoadProfiles();
            LstServers.ItemsSource = _profiles;
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            if (_profiles.Count > 0)
                LstServers.SelectedIndex = 0;

            _speedTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _speedTimer.Tick += SpeedTimer_Tick;

            // Сброс памяти сразу после полной отрисовки окна
            this.Loaded += (s, e) => TrimMemory();
        }

        private static void TrimMemory()
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
            }
        }

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
                MessageBox.Show($"Ресурс {fileName} не найден внутри сборки!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            using Stream? stream = assembly.GetManifestResourceStream(targetResource);
            if (stream != null)
            {
                using FileStream fileStream = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.CopyTo(fileStream);
            }
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                this.DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => this.Close();

        private void BtnToggleAddPanel_Click(object sender, RoutedEventArgs e)
        {
            if (PnlAddServer.Visibility == Visibility.Visible)
            {
                PnlAddServer.Visibility = Visibility.Collapsed;
                BtnToggleAddPanel.Content = "+ Добавить";
            }
            else
            {
                PnlAddServer.Visibility = Visibility.Visible;
                BtnToggleAddPanel.Content = "Скрыть форму";
                TxtInput.Focus();
            }
        }

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
            if (string.IsNullOrWhiteSpace(input))
            {
                MessageBox.Show("Введите ссылку или текст конфигурации!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnAdd.IsEnabled = false;
            try
            {
                if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    string rawSubscription = await SharedHttpClient.GetStringAsync(input);
                    string decoded = rawSubscription;
                    try
                    {
                        byte[] data = Convert.FromBase64String(rawSubscription.Trim());
                        decoded = Encoding.UTF8.GetString(data);
                    }
                    catch { }

                    var lines = decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    int addedCount = 0;
                    foreach (var line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                        {
                            AddVlessProfile(trimmed);
                            addedCount++;
                        }
                    }

                    if (addedCount == 0 && decoded.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                    {
                        AddVlessProfile(decoded.Trim());
                        addedCount++;
                    }

                    MessageBox.Show($"Успешно импортировано серверов: {addedCount}", "Импорт", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (input.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                {
                    AddVlessProfile(input);
                    MessageBox.Show("Сервер VLESS успешно добавлен!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    MessageBox.Show("Конфигурация WireGuard успешно добавлена!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Не удалось определить формат. Вставьте ссылку подписки, vless:// или WG конфиг.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                TxtInput.Clear();
                PnlAddServer.Visibility = Visibility.Collapsed;
                BtnToggleAddPanel.Content = "+ Добавить";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка импорта: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (_isConnected)
            {
                MessageBox.Show("Сначала отключите VPN!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (sender is FrameworkElement element && element.DataContext is VpnProfile profile)
            {
                _profiles.Remove(profile);
                SaveProfiles();
                if (_profiles.Count > 0)
                    LstServers.SelectedIndex = 0;
            }
        }

        private void LstServers_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Защита от смены сервера во время активного подключения
            if (_isConnected && _activeProfile != null && LstServers.SelectedItem != _activeProfile)
            {
                LstServers.SelectedItem = _activeProfile;
                return;
            }

            if (LstServers.SelectedItem is VpnProfile profile)
            {
                TxtStatus.Text = $"Выбран: {profile.Name} ({profile.Type.ToUpper()})";
            }
        }

        // ================= ПОДКЛЮЧЕНИЕ / ОТКЛЮЧЕНИЕ =================

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                if (LstServers.SelectedItem is not VpnProfile profile)
                {
                    MessageBox.Show("Сначала добавьте и выберите сервер!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _activeProfile = profile;
                BtnConnect.IsEnabled = false;
                TxtStatus.Text = "Подключение и проверка сети...";
                TxtStatus.Foreground = Brushes.Orange;

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
                        TxtStatus.Text = "Ошибка: сервер не отвечает";
                        TxtStatus.Foreground = Brushes.IndianRed;
                        MessageBox.Show(
                            "Служба запущена, но трафик через сервер не проходит.\n" +
                            "Возможные причины:\n" +
                            "• Закрыт порт на VPS (проверьте открыт ли UDP-порт для WireGuard)\n" +
                            "• Неверный приватный или публичный ключ\n" +
                            "• Ошибка в адресе Endpoint",
                            "Ошибка проверки сети", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    _isConnected = true;
                    BtnConnect.Content = "Отключиться";
                    BtnConnect.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F38BA8"));
                    TxtStatus.Text = $"Подключено: {profile.Name}";
                    TxtStatus.Foreground = Brushes.LightGreen;

                    _cachedInterfaces = NetworkInterface.GetAllNetworkInterfaces();
                    _lastBytesReceived = 0;
                    _lastBytesSent = 0;
                    PnlSpeed.Visibility = Visibility.Visible;
                    _speedTimer.Start();
                }
                catch (Exception ex)
                {
                    DisconnectAll();
                    TxtStatus.Text = "Ошибка запуска";
                    TxtStatus.Foreground = Brushes.IndianRed;
                    MessageBox.Show(ex.Message, "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
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
                BtnConnect.Content = "Подключиться";
                BtnConnect.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#89B4FA"));
                TxtStatus.Text = "Статус: Отключено";
                TxtStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAC2DE"));
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
                    {
                        return true;
                    }
                }
                catch
                {
                    await Task.Delay(800);
                }
            }

            return false;
        }

        private void SpeedTimer_Tick(object? sender, EventArgs e)
        {
            if (_cachedInterfaces == null) return;

            long currentReceived = 0;
            long currentSent = 0;

            for (int i = 0; i < _cachedInterfaces.Length; i++)
            {
                var ni = _cachedInterfaces[i];
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
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
                long diffReceived = Math.Max(0, currentReceived - _lastBytesReceived);
                long diffSent = Math.Max(0, currentSent - _lastBytesSent);

                double downSpeedMb = diffReceived / 1048576.0;
                double upSpeedMb = diffSent / 1048576.0;

                TxtDownloadSpeed.Text = $"↓ {downSpeedMb:F2} МБ/с";
                TxtUploadSpeed.Text = $"↑ {upSpeedMb:F2} МБ/с";
            }

            _lastBytesReceived = currentReceived;
            _lastBytesSent = currentSent;
        }

        // ================= VLESS =================

        private void StartXray(string vlessUrl)
        {
            string configJson = GenerateXrayConfig(vlessUrl);
            string configPath = Path.Combine(AppDataFolder, "config.json");
            File.WriteAllText(configPath, configJson);

            string xrayExe = Path.Combine(_coreDir, "xray.exe");
            if (!File.Exists(xrayExe))
                throw new FileNotFoundException("Файл xray.exe не найден!");

            ProcessStartInfo psi = new()
            {
                FileName = xrayExe,
                Arguments = $"run -config \"{configPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            psi.EnvironmentVariables["GOMEMLIMIT"] = "32MiB";
            psi.EnvironmentVariables["GOGC"] = "50";

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

        // ================= WIREGUARD (ПРЯМОЙ РАБОЧИЙ ШЛЮЗ) =================

        private void StartWireGuard(string confContent)
        {
            string configJson = GenerateWireGuardXrayConfig(confContent);
            string configPath = Path.Combine(AppDataFolder, "config.json");
            File.WriteAllText(configPath, configJson);

            string xrayExe = Path.Combine(_coreDir, "xray.exe");
            if (!File.Exists(xrayExe))
                throw new FileNotFoundException("Файл xray.exe не найден!");

            ProcessStartInfo psi = new()
            {
                FileName = xrayExe,
                Arguments = $"run -config \"{configPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            psi.EnvironmentVariables["GOMEMLIMIT"] = "32MiB";
            psi.EnvironmentVariables["GOGC"] = "50";

            _xrayProcess = Process.Start(psi);
            SetSystemProxy(true, "127.0.0.1:10809");
        }

        private string GenerateWireGuardXrayConfig(string confContent)
        {
            string privateKey = "";
            string address = "10.0.0.2/32";
            string endpoint = "";
            string publicKey = "";
            string presharedKey = "";
            int mtu = 1420;

            var lines = confContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("#") || !line.Contains('=')) continue;

                int eqIndex = line.IndexOf('=');
                string key = line.Substring(0, eqIndex).Trim();
                string val = line.Substring(eqIndex + 1).Trim();

                if (key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase))
                    privateKey = val;
                else if (key.Equals("Address", StringComparison.OrdinalIgnoreCase))
                    address = val.Contains('/') ? val : $"{val}/32";
                else if (key.Equals("PublicKey", StringComparison.OrdinalIgnoreCase))
                    publicKey = val;
                else if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
                    endpoint = val;
                else if (key.Equals("PresharedKey", StringComparison.OrdinalIgnoreCase))
                    presharedKey = val;
                else if (key.Equals("MTU", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int parsedMtu))
                    mtu = parsedMtu;
            }

            int lastColon = endpoint.LastIndexOf(':');
            string epHost = lastColon > 0 ? endpoint.Substring(0, lastColon).Trim() : endpoint;
            int epPort = lastColon > 0 && int.TryParse(endpoint.Substring(lastColon + 1).Trim(), out int p) ? p : 51820;

            string pskEntry = string.IsNullOrWhiteSpace(presharedKey) ? "" : $"\"preSharedKey\": \"{presharedKey}\",";

            // Рабочий конфиг WireGuard без конфликтующего внутреннего DNS
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
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (key != null)
            {
                key.SetValue("ProxyEnable", enable ? 1 : 0);
                if (enable)
                    key.SetValue("ProxyServer", proxyServer);

                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            DisconnectAll();
        }
    }
}