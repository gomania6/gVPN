using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace gVPN
{
    [SupportedOSPlatform("windows")]
    public static class SettingsService
    {
        public static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gVPN");

        private static readonly string StoragePath = Path.Combine(AppDataFolder, "servers.json");
        private static readonly string SettingsPath = Path.Combine(AppDataFolder, "settings.json");
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static void EnsureDirectoryCreated()
        {
            if (!Directory.Exists(AppDataFolder))
                Directory.CreateDirectory(AppDataFolder);
        }

        public static List<VpnProfile> LoadProfiles()
        {
            try
            {
                if (File.Exists(StoragePath))
                {
                    string json = File.ReadAllText(StoragePath);
                    return JsonSerializer.Deserialize<List<VpnProfile>>(json, JsonOpts) ?? new();
                }
            }
            catch { }
            return new();
        }

        public static void SaveProfiles(IEnumerable<VpnProfile> profiles)
        {
            try
            {
                EnsureDirectoryCreated();
                string json = JsonSerializer.Serialize(profiles, JsonOpts);
                File.WriteAllText(StoragePath, json);
            }
            catch { }
        }

        public static AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new();
                }
            }
            catch { }
            return new();
        }

        public static void SaveSettings(AppSettings settings)
        {
            try
            {
                EnsureDirectoryCreated();
                string json = JsonSerializer.Serialize(settings, JsonOpts);
                File.WriteAllText(SettingsPath, json);
                ApplyWindowsAutoStart(settings.AutoStartApp);
            }
            catch { }
        }

        public static void ApplyWindowsAutoStart(bool enable)
        {
            try
            {
                string? appPath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(appPath)) return;

                // Очищаем старую запись в реестре Run (т.к. с requireAdministrator в манифесте Windows блокирует Run)
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key?.GetValue("gVPN") != null)
                        key.DeleteValue("gVPN", false);
                }

                // Настраиваем Планировщик задач с наивысшими правами (/RL HIGHEST), чтобы приложение с правами админа стартовало без ошибок
                string args = enable
                    ? $"/Create /F /RL HIGHEST /SC ONLOGON /TN \"gVPN_AutoStart\" /TR \"\\\"{appPath}\\\" --autostart\""
                    : "/Delete /F /TN \"gVPN_AutoStart\"";

                ProcessStartInfo psi = new()
                {
                    FileName = "schtasks.exe",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
            }
            catch { }
        }
    }
}