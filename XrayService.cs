using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Win32;

namespace gVPN
{
    [SupportedOSPlatform("windows")]
    public class XrayService
    {
        private readonly string _coreDir = Path.Combine(SettingsService.AppDataFolder, "Core");
        private readonly string _configPath = Path.Combine(SettingsService.AppDataFolder, "config.json");
        private Process? _xrayProcess;
        private SplitTunnelProxyServer? _splitProxy;

        private const int XraySocksPort = 10808;
        private const int XrayHttpPort = 10809;
        private const int SplitRouterPort = 10810;

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        public bool IsRunning => _xrayProcess != null && !_xrayProcess.HasExited;

        public void EnsureCoreExtracted()
        {
            if (!Directory.Exists(_coreDir))
                Directory.CreateDirectory(_coreDir);

            string destinationPath = Path.Combine(_coreDir, "xray.exe");
            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
                return;

            var assembly = Assembly.GetExecutingAssembly();
            string? targetResource = assembly.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith("xray.exe", StringComparison.OrdinalIgnoreCase));

            if (targetResource == null) return;

            using Stream? stream = assembly.GetManifestResourceStream(targetResource);
            if (stream != null)
            {
                using FileStream fileStream = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.CopyTo(fileStream);
            }
        }

        public void Start(VpnProfile profile, bool enableGlobalProxy, IEnumerable<string>? customAppList = null)
        {
            Stop();
            KillOrphanedXrayProcesses();
            SettingsService.EnsureDirectoryCreated();
            EnsureCoreExtracted();

            string configJson = profile.Type.ToLowerInvariant() switch
            {
                "vless" => GenerateVlessConfig(profile.RawData),
                "trojan" => GenerateTrojanConfig(profile.RawData),
                "shadowsocks" => GenerateShadowsocksConfig(profile.RawData),
                _ => GenerateWireGuardConfig(profile.RawData)
            };

            File.WriteAllText(_configPath, configJson, new UTF8Encoding(false));

            string xrayExe = Path.Combine(_coreDir, "xray.exe");
            if (!File.Exists(xrayExe))
                throw new FileNotFoundException("Файл ядра xray.exe не найден!");

            ProcessStartInfo psi = new()
            {
                FileName = xrayExe,
                Arguments = $"run -config \"{_configPath}\"",
                WorkingDirectory = _coreDir,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            psi.EnvironmentVariables["GOMEMLIMIT"] = "32MiB";
            psi.EnvironmentVariables["GOGC"] = "30";

            _xrayProcess = Process.Start(psi);

            if (enableGlobalProxy)
            {
                SetSystemProxy(true, $"127.0.0.1:{XrayHttpPort}");
            }
            else
            {
                _splitProxy = new SplitTunnelProxyServer(SplitRouterPort, XrayHttpPort, XraySocksPort, customAppList);
                _splitProxy.Start();
                SetSystemProxy(true, $"127.0.0.1:{SplitRouterPort}");
            }
        }

        public void Stop()
        {
            try
            {
                _splitProxy?.Stop();
                _splitProxy = null;
            }
            catch { }

            if (_xrayProcess != null)
            {
                try
                {
                    if (!_xrayProcess.HasExited)
                        _xrayProcess.Kill(true);
                }
                catch { }
                finally
                {
                    _xrayProcess.Dispose();
                    _xrayProcess = null;
                }
            }

            SetSystemProxy(false);
        }

        private static void KillOrphanedXrayProcesses()
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("xray"))
                {
                    try
                    {
                        proc.Kill(true);
                        proc.Dispose();
                    }
                    catch { }
                }
            }
            catch { }
        }

        public async Task<bool> VerifyConnectionAsync()
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (!IsRunning) return false;
                try
                {
                    using var handler = new HttpClientHandler
                    {
                        Proxy = new WebProxy($"http://127.0.0.1:{XrayHttpPort}"),
                        UseProxy = true
                    };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
                    using var response = await client.GetAsync("http://cp.cloudflare.com/generate_204");
                    if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent)
                        return true;
                }
                catch
                {
                    await Task.Delay(600);
                }
            }
            return false;
        }

        private static void SetSystemProxy(bool enable, string proxyServer = "")
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
                if (key != null)
                {
                    key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
                    if (enable)
                    {
                        key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
                        key.SetValue("ProxyOverride", "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>", RegistryValueKind.String);
                    }
                    InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                    InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
                }
            }
            catch { }
        }

        private static object[] BuildInbounds() => new object[]
        {
            new { tag = "socks-in", port = XraySocksPort, listen = "127.0.0.1", protocol = "socks", settings = new { auth = "noauth", udp = true } },
            new { tag = "http-in", port = XrayHttpPort, listen = "127.0.0.1", protocol = "http" }
        };

        private static object BuildRouting(string outboundTag) => new
        {
            domainStrategy = "IPIfNonMatch",
            rules = new[]
            {
                new { type = "field", inboundTag = new[] { "socks-in", "http-in" }, outboundTag }
            }
        };

        private string GenerateVlessConfig(string vlessUrl)
        {
            var uri = new Uri(vlessUrl.Trim());
            string uuid = Uri.UnescapeDataString(uri.UserInfo);
            string host = uri.Host;
            int port = uri.Port > 0 ? uri.Port : 443;
            var q = HttpUtility.ParseQueryString(uri.Query);

            string network = (q["type"] ?? "tcp").ToLowerInvariant();
            string security = (q["security"] ?? "reality").ToLowerInvariant();
            string flow = q["flow"] ?? (security == "reality" && network == "tcp" ? "xtls-rprx-vision" : "");
            string fp = q["fp"] ?? "chrome";
            string sni = q["sni"] ?? "dl.google.com";
            string pbk = q["pbk"] ?? "";
            string sid = q["sid"] ?? "";
            string spx = q["spx"] ?? "";
            string path = q["path"] ?? "/";
            string serviceName = q["serviceName"] ?? "";

            var config = new
            {
                log = new { loglevel = "warning" },
                inbounds = BuildInbounds(),
                outbounds = new object[]
                {
                    new
                    {
                        tag = "proxy",
                        protocol = "vless",
                        settings = new
                        {
                            vnext = new[]
                            {
                                new
                                {
                                    address = host,
                                    port = port,
                                    users = new[]
                                    {
                                        new { id = uuid, encryption = q["encryption"] ?? "none", flow = flow }
                                    }
                                }
                            }
                        },
                        streamSettings = new
                        {
                            network = network,
                            security = security,
                            realitySettings = security == "reality" ? new
                            {
                                show = false,
                                fingerprint = fp,
                                serverName = sni,
                                publicKey = pbk,
                                shortId = sid,
                                spiderX = spx
                            } : null,
                            tlsSettings = security == "tls" ? new
                            {
                                serverName = sni,
                                fingerprint = fp,
                                allowInsecure = false
                            } : null,
                            wsSettings = network == "ws" ? new
                            {
                                path = path,
                                headers = new { Host = q["host"] ?? sni }
                            } : null,
                            grpcSettings = network == "grpc" ? new
                            {
                                serviceName = serviceName
                            } : null
                        }
                    },
                    new { tag = "direct", protocol = "freedom" }
                },
                routing = BuildRouting("proxy")
            };

            return JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }

        private string GenerateTrojanConfig(string trojanUrl)
        {
            var uri = new Uri(trojanUrl.Trim());
            string password = Uri.UnescapeDataString(uri.UserInfo);
            string host = uri.Host;
            int port = uri.Port > 0 ? uri.Port : 443;
            var q = HttpUtility.ParseQueryString(uri.Query);

            string network = (q["type"] ?? "tcp").ToLowerInvariant();
            string sni = q["sni"] ?? host;
            string fp = q["fp"] ?? "chrome";

            var config = new
            {
                log = new { loglevel = "warning" },
                inbounds = BuildInbounds(),
                outbounds = new object[]
                {
                    new
                    {
                        tag = "proxy",
                        protocol = "trojan",
                        settings = new
                        {
                            servers = new[] { new { address = host, port = port, password = password } }
                        },
                        streamSettings = new
                        {
                            network = network,
                            security = "tls",
                            tlsSettings = new { serverName = sni, fingerprint = fp, allowInsecure = false },
                            wsSettings = network == "ws" ? new { path = q["path"] ?? "/", headers = new { Host = q["host"] ?? sni } } : null
                        }
                    },
                    new { tag = "direct", protocol = "freedom" }
                },
                routing = BuildRouting("proxy")
            };

            return JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }

        private string GenerateShadowsocksConfig(string ssUrl)
        {
            var uri = new Uri(ssUrl.Trim());
            string userInfo = Uri.UnescapeDataString(uri.UserInfo);
            if (!userInfo.Contains(':'))
            {
                string padded = userInfo.PadRight(userInfo.Length + (4 - userInfo.Length % 4) % 4, '=');
                userInfo = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            }

            int colon = userInfo.IndexOf(':');
            string method = userInfo[..colon];
            string password = userInfo[(colon + 1)..];

            var config = new
            {
                log = new { loglevel = "warning" },
                inbounds = BuildInbounds(),
                outbounds = new object[]
                {
                    new
                    {
                        tag = "proxy",
                        protocol = "shadowsocks",
                        settings = new
                        {
                            servers = new[] { new { address = uri.Host, port = uri.Port, method = method, password = password } }
                        }
                    },
                    new { tag = "direct", protocol = "freedom" }
                },
                routing = BuildRouting("proxy")
            };

            return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        }

        private string GenerateWireGuardConfig(string conf)
        {
            string priv = "", ep = "", pub = "", psk = "";
            List<string> addresses = new();
            int mtu = 1420;

            foreach (var rawLine in conf.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var l = rawLine.Trim();
                if (l.StartsWith("#") || l.StartsWith("[") || !l.Contains('=')) continue;
                int eq = l.IndexOf('=');
                string k = l[..eq].Trim();
                string v = l[(eq + 1)..].Trim();

                if (k.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase)) priv = v;
                else if (k.Equals("Address", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string clean = part.Trim();
                        if (!clean.Contains('/'))
                            clean += clean.Contains(':') ? "/128" : "/32";
                        addresses.Add(clean);
                    }
                }
                else if (k.Equals("PublicKey", StringComparison.OrdinalIgnoreCase)) pub = v;
                else if (k.Equals("Endpoint", StringComparison.OrdinalIgnoreCase)) ep = v;
                else if (k.Equals("PresharedKey", StringComparison.OrdinalIgnoreCase)) psk = v;
                else if (k.Equals("MTU", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out int m)) mtu = m;
            }

            if (addresses.Count == 0)
                addresses.Add("10.0.0.2/32");

            int colon = ep.LastIndexOf(':');
            string host = colon > 0 ? ep[..colon].Trim() : ep;
            int port = colon > 0 && int.TryParse(ep[(colon + 1)..].Trim(), out int p) ? p : 51820;

            var peer = new Dictionary<string, object>
            {
                ["publicKey"] = pub,
                ["endpoint"] = $"{host}:{port}"
            };
            if (!string.IsNullOrWhiteSpace(psk))
                peer["preSharedKey"] = psk;

            var config = new
            {
                log = new { loglevel = "warning" },
                dns = new { servers = new[] { "1.1.1.1", "8.8.8.8" } },
                inbounds = BuildInbounds(),
                outbounds = new object[]
                {
                    new
                    {
                        tag = "wireguard-out",
                        protocol = "wireguard",
                        settings = new
                        {
                            secretKey = priv,
                            address = addresses,
                            peers = new[] { peer },
                            mtu = mtu
                        }
                    },
                    new { tag = "direct", protocol = "freedom" }
                },
                routing = BuildRouting("wireguard-out")
            };

            return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    [SupportedOSPlatform("windows")]
    internal sealed class SplitTunnelProxyServer
    {
        private readonly int _listenPort;
        private readonly int _xrayHttpPort;
        private readonly int _xraySocksPort;
        private readonly HashSet<string> _allowedExeNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _allowedFullPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private DateTime _lastSettingsLoad = DateTime.MinValue;

        public SplitTunnelProxyServer(int listenPort, int xrayHttpPort, int xraySocksPort, IEnumerable<string>? initialApps)
        {
            _listenPort = listenPort;
            _xrayHttpPort = xrayHttpPort;
            _xraySocksPort = xraySocksPort;
            ReloadAllowedApps(initialApps);
        }

        private void ReloadAllowedApps(IEnumerable<string>? customApps = null)
        {
            lock (_lock)
            {
                if (customApps == null && (DateTime.UtcNow - _lastSettingsLoad).TotalSeconds < 2)
                    return;

                _lastSettingsLoad = DateTime.UtcNow;
                _allowedExeNames.Clear();
                _allowedFullPaths.Clear();

                var collected = new List<string>();
                if (customApps != null)
                    collected.AddRange(customApps);

                try
                {
                    var settings = SettingsService.LoadSettings();
                    if (settings.TargetAppPaths != null)
                        collected.AddRange(settings.TargetAppPaths);
                }
                catch { }

                foreach (var app in collected)
                {
                    if (string.IsNullOrWhiteSpace(app)) continue;
                    string trimmed = app.Trim().Trim('"');
                    _allowedFullPaths.Add(trimmed);

                    string fileName = Path.GetFileName(trimmed);
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        _allowedExeNames.Add(fileName);
                        string withoutExt = Path.GetFileNameWithoutExtension(fileName);
                        if (!string.IsNullOrEmpty(withoutExt))
                            _allowedExeNames.Add(withoutExt);
                    }
                }
            }
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.IPv6Any, _listenPort);
            _listener.Server.DualMode = true;
            _listener.Start();
            _ = AcceptLoopAsync(_cts.Token);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    if (client.Client.RemoteEndPoint is not IPEndPoint clientEp)
                        return;

                    ReloadAllowedApps();
                    bool routeViaVpn = ShouldRoutePortViaVpn(clientEp.Port);

                    using NetworkStream clientStream = client.GetStream();
                    byte[] firstByte = new byte[1];
                    int readFirst = await clientStream.ReadAsync(firstByte.AsMemory(0, 1), ct);
                    if (readFirst <= 0) return;

                    bool isSocks5 = firstByte[0] == 0x05;

                    if (routeViaVpn)
                    {
                        int targetXrayPort = isSocks5 ? _xraySocksPort : _xrayHttpPort;
                        using TcpClient xrayClient = new();
                        await xrayClient.ConnectAsync(IPAddress.Loopback, targetXrayPort, ct);
                        using NetworkStream xrayStream = xrayClient.GetStream();
                        await xrayStream.WriteAsync(firstByte.AsMemory(0, 1), ct);
                        await RelayBidirectionalAsync(clientStream, xrayStream, ct);
                    }
                    else
                    {
                        if (!isSocks5)
                            await HandleDirectHttpProxyAsync(clientStream, firstByte[0], ct);
                        else
                            await HandleDirectSocks5ProxyAsync(clientStream, ct);
                    }
                }
                catch { }
            }
        }

        private async Task HandleDirectSocks5ProxyAsync(NetworkStream clientStream, CancellationToken ct)
        {
            byte[] buf = new byte[260];
            if (await clientStream.ReadAsync(buf.AsMemory(0, 1), ct) <= 0) return;
            int nMethods = buf[0];
            if (await ReadExactAsync(clientStream, buf, nMethods, ct) < nMethods) return;

            await clientStream.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

            if (await ReadExactAsync(clientStream, buf, 4, ct) < 4) return;
            if (buf[1] != 0x01) return;

            string host;
            int atyp = buf[3];
            if (atyp == 0x01)
            {
                if (await ReadExactAsync(clientStream, buf, 4, ct) < 4) return;
                host = new IPAddress(buf.AsSpan(0, 4)).ToString();
            }
            else if (atyp == 0x03)
            {
                if (await ReadExactAsync(clientStream, buf, 1, ct) < 1) return;
                int len = buf[0];
                if (await ReadExactAsync(clientStream, buf, len, ct) < len) return;
                host = Encoding.ASCII.GetString(buf, 0, len);
            }
            else if (atyp == 0x04)
            {
                if (await ReadExactAsync(clientStream, buf, 16, ct) < 16) return;
                host = new IPAddress(buf.AsSpan(0, 16)).ToString();
            }
            else return;

            if (await ReadExactAsync(clientStream, buf, 2, ct) < 2) return;
            int port = (buf[0] << 8) | buf[1];

            using TcpClient directClient = new();
            await directClient.ConnectAsync(host, port, ct);
            using NetworkStream directStream = directClient.GetStream();

            byte[] reply = { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
            await clientStream.WriteAsync(reply, ct);
            await RelayBidirectionalAsync(clientStream, directStream, ct);
        }

        private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int offset = 0;
            while (offset < count)
            {
                int r = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
                if (r <= 0) break;
                offset += r;
            }
            return offset;
        }

        private async Task HandleDirectHttpProxyAsync(NetworkStream clientStream, byte firstByte, CancellationToken ct)
        {
            byte[] headerBuf = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                headerBuf[0] = firstByte;
                int totalRead = 1;
                int headerEnd = -1;

                while (totalRead < headerBuf.Length)
                {
                    int read = await clientStream.ReadAsync(headerBuf.AsMemory(totalRead, headerBuf.Length - totalRead), ct);
                    if (read <= 0) return;
                    totalRead += read;
                    headerEnd = FindHeaderEnd(headerBuf, totalRead);
                    if (headerEnd >= 0) break;
                }

                if (headerEnd < 0) return;

                string headerText = Encoding.ASCII.GetString(headerBuf, 0, headerEnd);
                string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;

                string[] firstLineParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (firstLineParts.Length < 2) return;

                string method = firstLineParts[0];
                string target = firstLineParts[1];

                if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    string host = target;
                    int port = 443;
                    int colon = target.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(target[(colon + 1)..], out int p))
                    {
                        host = target[..colon].Trim('[', ']');
                        port = p;
                    }

                    using TcpClient directClient = new();
                    await directClient.ConnectAsync(host, port, ct);
                    using NetworkStream directStream = directClient.GetStream();

                    byte[] okResponse = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                    await clientStream.WriteAsync(okResponse, ct);

                    int bodyOffset = headerEnd + 4;
                    if (totalRead > bodyOffset)
                        await directStream.WriteAsync(headerBuf.AsMemory(bodyOffset, totalRead - bodyOffset), ct);

                    await RelayBidirectionalAsync(clientStream, directStream, ct);
                }
                else
                {
                    if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri))
                    {
                        string? hostHeader = lines.FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
                        if (hostHeader == null) return;
                        string hostVal = hostHeader[5..].Trim();
                        uri = new Uri($"http://{hostVal}{target}");
                    }

                    using TcpClient directClient = new();
                    await directClient.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 80, ct);
                    using NetworkStream directStream = directClient.GetStream();

                    string relativeFirstLine = $"{method} {uri.PathAndQuery} {(firstLineParts.Length > 2 ? firstLineParts[2] : "HTTP/1.1")}\r\n";
                    string restHeaders = string.Join("\r\n", lines.Skip(1).Where(l => !l.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))) + "\r\n\r\n";
                    byte[] rebuiltHeader = Encoding.ASCII.GetBytes(relativeFirstLine + restHeaders);
                    await directStream.WriteAsync(rebuiltHeader, ct);

                    int bodyOffset = headerEnd + 4;
                    if (totalRead > bodyOffset)
                        await directStream.WriteAsync(headerBuf.AsMemory(bodyOffset, totalRead - bodyOffset), ct);

                    await RelayBidirectionalAsync(clientStream, directStream, ct);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headerBuf);
            }
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - 4; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return i;
            }
            return -1;
        }

        private static async Task RelayBidirectionalAsync(Stream s1, Stream s2, CancellationToken ct)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var t1 = s1.CopyToAsync(s2, linkedCts.Token);
            var t2 = s2.CopyToAsync(s1, linkedCts.Token);
            await Task.WhenAny(t1, t2);
            linkedCts.Cancel();
            try { s1.Close(); } catch { }
            try { s2.Close(); } catch { }
        }

        private bool ShouldRoutePortViaVpn(int clientLocalPort)
        {
            int pid = GetProcessIdByLocalPort(clientLocalPort);
            if (pid <= 0) return false;
            return IsPidAllowed(pid) || IsParentPidAllowed(pid);
        }

        private bool IsPidAllowed(int pid)
        {
            if (pid <= 4) return false;

            string? fullPath = TryGetProcessPath(pid);
            string? exeName = !string.IsNullOrEmpty(fullPath) ? Path.GetFileName(fullPath) : null;
            string? exeNoExt = !string.IsNullOrEmpty(exeName) ? Path.GetFileNameWithoutExtension(exeName) : null;

            if (string.IsNullOrEmpty(exeName))
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    exeNoExt = proc.ProcessName;
                    exeName = proc.ProcessName + ".exe";
                }
                catch { }
            }

            lock (_lock)
            {
                if (!string.IsNullOrEmpty(fullPath) && _allowedFullPaths.Contains(fullPath))
                    return true;
                if (!string.IsNullOrEmpty(exeName) && _allowedExeNames.Contains(exeName))
                    return true;
                if (!string.IsNullOrEmpty(exeNoExt) && _allowedExeNames.Contains(exeNoExt))
                    return true;
            }
            return false;
        }

        private bool IsParentPidAllowed(int childPid)
        {
            int parentPid = GetParentProcessId(childPid);
            if (parentPid <= 4 || parentPid == childPid) return false;
            return IsPidAllowed(parentPid);
        }

        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        private static int GetParentProcessId(int pid)
        {
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return -1;
            try
            {
                PROCESS_BASIC_INFORMATION pbi = new();
                int status = NtQueryInformationProcess(hProc, 0, ref pbi, Marshal.SizeOf(pbi), out _);
                return status == 0 ? pbi.InheritedFromUniqueProcessId.ToInt32() : -1;
            }
            catch { return -1; }
            finally { CloseHandle(hProc); }
        }

        private static string? TryGetProcessPath(int pid)
        {
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(hProc, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(hProc); }
        }

        private static int GetProcessIdByLocalPort(int localPort)
        {
            int pid = GetProcessIdByLocalPortIPv4(localPort);
            if (pid > 0) return pid;
            return GetProcessIdByLocalPortIPv6(localPort);
        }

        private static int GetProcessIdByLocalPortIPv4(int localPort)
        {
            int bufferSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
            if (bufferSize <= 0) return -1;

            IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (GetExtendedTcpTable(tcpTablePtr, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0)
                    return -1;

                int rowCount = Marshal.ReadInt32(tcpTablePtr);
                IntPtr rowPtr = IntPtr.Add(tcpTablePtr, 4);
                for (int i = 0; i < rowCount; i++)
                {
                    int rawLocalPort = Marshal.ReadInt32(rowPtr, 8);
                    int port = ((rawLocalPort & 0xFF) << 8) | ((rawLocalPort >> 8) & 0xFF);
                    if (port == localPort)
                        return Marshal.ReadInt32(rowPtr, 20);
                    rowPtr = IntPtr.Add(rowPtr, 24);
                }
            }
            finally { Marshal.FreeHGlobal(tcpTablePtr); }
            return -1;
        }

        private static int GetProcessIdByLocalPortIPv6(int localPort)
        {
            int bufferSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET6, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
            if (bufferSize <= 0) return -1;

            IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (GetExtendedTcpTable(tcpTablePtr, ref bufferSize, false, AF_INET6, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0)
                    return -1;

                int rowCount = Marshal.ReadInt32(tcpTablePtr);
                IntPtr rowPtr = IntPtr.Add(tcpTablePtr, 4);
                for (int i = 0; i < rowCount; i++)
                {
                    int rawLocalPort = Marshal.ReadInt32(rowPtr, 20);
                    int port = ((rawLocalPort & 0xFF) << 8) | ((rawLocalPort >> 8) & 0xFF);
                    if (port == localPort)
                        return Marshal.ReadInt32(rowPtr, 52);
                    rowPtr = IntPtr.Add(rowPtr, 56);
                }
            }
            finally { Marshal.FreeHGlobal(tcpTablePtr); }
            return -1;
        }
    }
}