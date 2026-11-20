using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace gVPN
{
    public static class PulseGuardState
    {
        public const string Disconnected = "disconnected";
        public const string Connected = "connected";
        public const string Error = "error";
    }

    public sealed class PulseGuardStatusPayload
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = PulseGuardState.Disconnected;

        [JsonPropertyName("server")]
        public string Server { get; set; } = "";

        [JsonPropertyName("countryCode")]
        public string CountryCode { get; set; } = "";

        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = "";

        [JsonPropertyName("externalIp")]
        public string ExternalIp { get; set; } = "";

        [JsonPropertyName("updatedAt")]
        public string UpdatedAt { get; set; } = "";
    }

    [SupportedOSPlatform("windows")]
    public static class PulseGuardService
    {
        private static readonly string PulseGuardFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PulseGuard");

        private static readonly string StatusFilePath = Path.Combine(PulseGuardFolder, "vpn_status.json");
        private static readonly object FileLock = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static CancellationTokenSource? _ipLookupCts;
        private static string _lastKnownExternalIp = "";
        private static string _lastKnownCountryCode = "";

        /// <summary>
        /// Writes the current VPN state atomically to %AppData%\PulseGuard\vpn_status.json.
        /// </summary>
        public static void WriteStatus(string state, VpnProfile? profile, string? externalIp = null, string? countryCode = null)
        {
            if (state != PulseGuardState.Connected)
            {
                CancelPendingIpLookup();
                _lastKnownExternalIp = "";
                _lastKnownCountryCode = "";
            }

            string resolvedCountry = !string.IsNullOrWhiteSpace(countryCode)
                ? countryCode.Trim().ToUpperInvariant()
                : (!string.IsNullOrWhiteSpace(_lastKnownCountryCode) && state == PulseGuardState.Connected
                    ? _lastKnownCountryCode
                    : ExtractCountryCodeFromName(profile?.Name));

            string resolvedIp = externalIp ?? (state == PulseGuardState.Connected ? _lastKnownExternalIp : "");

            var payload = new PulseGuardStatusPayload
            {
                State = state,
                Server = profile?.Name ?? "",
                CountryCode = resolvedCountry,
                Protocol = profile?.Type ?? "",
                ExternalIp = resolvedIp,
                UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };

            WriteAtomic(payload);
        }

        /// <summary>
        /// Resolves the external IP and country code through the active local proxy and updates the status file.
        /// </summary>
        public static void RefreshExternalIpAsync(VpnProfile profile, Func<bool> isStillConnected)
        {
            CancelPendingIpLookup();
            var cts = new CancellationTokenSource();
            _ipLookupCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    var (ip, country) = await FetchExternalIpInfoAsync(cts.Token).ConfigureAwait(false);
                    if (cts.Token.IsCancellationRequested || !isStillConnected())
                        return;

                    if (!string.IsNullOrWhiteSpace(ip) || !string.IsNullOrWhiteSpace(country))
                    {
                        _lastKnownExternalIp = ip ?? "";
                        _lastKnownCountryCode = country ?? "";
                        WriteStatus(PulseGuardState.Connected, profile, _lastKnownExternalIp, _lastKnownCountryCode);
                    }
                }
                catch
                {
                    // Ignore transient IP lookup failures; status file already reflects "connected".
                }
            }, cts.Token);
        }

        private static void CancelPendingIpLookup()
        {
            try
            {
                _ipLookupCts?.Cancel();
                _ipLookupCts?.Dispose();
            }
            catch { }
            finally
            {
                _ipLookupCts = null;
            }
        }

        private static void WriteAtomic(PulseGuardStatusPayload payload)
        {
            lock (FileLock)
            {
                try
                {
                    if (!Directory.Exists(PulseGuardFolder))
                        Directory.CreateDirectory(PulseGuardFolder);

                    string json = JsonSerializer.Serialize(payload, JsonOptions);
                    string tempPath = Path.Combine(PulseGuardFolder, $"vpn_status.{Guid.NewGuid():N}.tmp");

                    File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                    File.Move(tempPath, StatusFilePath, overwrite: true);
                }
                catch
                {
                    // Never crash the VPN client on status file I/O errors.
                }
            }
        }

        private static async Task<(string? Ip, string? CountryCode)> FetchExternalIpInfoAsync(CancellationToken token)
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy("http://127.0.0.1:10809"),
                UseProxy = true
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };

            try
            {
                string json = await client.GetStringAsync("http://ip-api.com/json/?fields=query,countryCode", token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                string? ip = doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() : null;
                string? cc = doc.RootElement.TryGetProperty("countryCode", out var c) ? c.GetString() : null;
                if (!string.IsNullOrWhiteSpace(ip))
                    return (ip, cc);
            }
            catch { }

            try
            {
                string ip = (await client.GetStringAsync("https://api.ipify.org", token).ConfigureAwait(false)).Trim();
                if (!string.IsNullOrWhiteSpace(ip))
                    return (ip, null);
            }
            catch { }

            return (null, null);
        }

        private static string ExtractCountryCodeFromName(string? serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
                return "";

            // Check for regional indicator flag emoji pairs (U+1F1E6..U+1F1FF -> 'A'..'Z')
            for (int i = 0; i < serverName.Length - 1; i++)
            {
                if (char.IsHighSurrogate(serverName[i]) && char.IsLowSurrogate(serverName[i + 1]))
                {
                    int cp1 = char.ConvertToUtf32(serverName[i], serverName[i + 1]);
                    if (cp1 is >= 0x1F1E6 and <= 0x1F1FF && i + 3 < serverName.Length &&
                        char.IsHighSurrogate(serverName[i + 2]) && char.IsLowSurrogate(serverName[i + 3]))
                    {
                        int cp2 = char.ConvertToUtf32(serverName[i + 2], serverName[i + 3]);
                        if (cp2 is >= 0x1F1E6 and <= 0x1F1FF)
                        {
                            char c1 = (char)('A' + (cp1 - 0x1F1E6));
                            char c2 = (char)('A' + (cp2 - 0x1F1E6));
                            return new string(new[] { c1, c2 });
                        }
                    }
                }
            }

            // Fallback: standalone 2-letter uppercase ISO-like token e.g. [NL], (DE), "NL - Amsterdam"
            var match = Regex.Match(serverName, @"(?<![A-Za-z])([A-Z]{2})(?![A-Za-z])");
            return match.Success ? match.Groups[1].Value : "";
        }
    }
}

