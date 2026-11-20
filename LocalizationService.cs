using System.Runtime.Versioning;
using System.Windows;

namespace gVPN
{
    public sealed record LocalizedStrings(
        string Connect,
        string Disconnect,
        string StatusDisconnected,
        string StatusConnectedFormat,
        string StatusConnecting,
        string StatusSelectedFormat,
        string StatusError,
        string TrayStatusDisconnected,
        string TrayOpen,
        string TrayExit,
        string SettingsTitle,
        string VpnErrorTitle,
        string XrayExitedError,
        string ServerTimeoutError,
        string LaunchApp,
        string TooltipSettings,
        string TooltipMinimize,
        string TooltipClose,
        string TooltipDelete,
        string TooltipRename,
        string PingAll,
        string PingTimeout
    );

    [SupportedOSPlatform("windows")]
    public static class LocalizationService
    {
        public static LocalizedStrings GetStrings(string lang) => lang switch
        {
            "en" => new LocalizedStrings(
                Connect: "Connect",
                Disconnect: "Disconnect",
                StatusDisconnected: "Status: Disconnected",
                StatusConnectedFormat: "Connected: {0}",
                StatusConnecting: "Connecting and verifying...",
                StatusSelectedFormat: "Selected: {0} ({1})",
                StatusError: "Connection error",
                TrayStatusDisconnected: "gVPN: Disconnected",
                TrayOpen: "Open gVPN",
                TrayExit: "Exit gVPN",
                SettingsTitle: "Settings",
                VpnErrorTitle: "VPN Error",
                XrayExitedError: "xray.exe core exited unexpectedly. Check your server config.",
                ServerTimeoutError: "No response from remote server (verification timeout).",
                LaunchApp: "Launch",
                TooltipSettings: "Settings",
                TooltipMinimize: "Minimize to tray",
                TooltipClose: "Hide",
                TooltipDelete: "Delete",
                TooltipRename: "Rename",
                PingAll: "Ping",
                PingTimeout: "timeout"
            ),
            "pl" => new LocalizedStrings(
                Connect: "Połącz",
                Disconnect: "Rozłącz",
                StatusDisconnected: "Status: Rozłączono",
                StatusConnectedFormat: "Połączono: {0}",
                StatusConnecting: "Łączenie i weryfikacja...",
                StatusSelectedFormat: "Wybrano: {0} ({1})",
                StatusError: "Błąd połączenia",
                TrayStatusDisconnected: "gVPN: Rozłączono",
                TrayOpen: "Otwórz gVPN",
                TrayExit: "Zamknij gVPN",
                SettingsTitle: "Ustawienia",
                VpnErrorTitle: "Błąd VPN",
                XrayExitedError: "Rdzeń xray.exe zakończył pracę. Sprawdź poprawność klucza.",
                ServerTimeoutError: "Brak odpowiedzi ze zdalnego serwera (limit czasu).",
                LaunchApp: "Uruchom",
                TooltipSettings: "Ustawienia",
                TooltipMinimize: "Minimalizuj do zasobnika",
                TooltipClose: "Ukryj",
                TooltipDelete: "Usuń",
                TooltipRename: "Zmień nazwę",
                PingAll: "Ping",
                PingTimeout: "limit czasu"
            ),
            _ => new LocalizedStrings(
                Connect: "Подключить",
                Disconnect: "Отключить",
                StatusDisconnected: "Статус: Отключено",
                StatusConnectedFormat: "Подключено: {0}",
                StatusConnecting: "Подключение и проверка связи...",
                StatusSelectedFormat: "Выбран: {0} ({1})",
                StatusError: "Ошибка подключения",
                TrayStatusDisconnected: "gVPN: Отключено",
                TrayOpen: "Открыть gVPN",
                TrayExit: "Выйти из gVPN",
                SettingsTitle: "Настройки",
                VpnErrorTitle: "Ошибка VPN",
                XrayExitedError: "Ядро xray.exe завершило работу. Проверьте корректность ключа.",
                ServerTimeoutError: "Нет ответа от удалённого сервера (таймаут проверки).",
                LaunchApp: "Запуск",
                TooltipSettings: "Настройки",
                TooltipMinimize: "Свернуть в трей",
                TooltipClose: "Скрыть",
                TooltipDelete: "Удалить",
                TooltipRename: "Переименовать",
                PingAll: "Пинг",
                PingTimeout: "таймаут"
            )
        };

        public static void UpdateUi(MainWindow w, string lang, bool isConnected, string? profileName)
        {
            var s = GetStrings(lang);

            switch (lang)
            {
                case "en":
                    w.LblAvailableServers.Text = "Available Servers";
                    w.BtnToggleAddPanel.Content = w.PnlAddServer.Visibility == Visibility.Visible ? "Hide Form" : "+ Add";
                    w.LblAddHint.Text = "Paste vless://, trojan://, ss://, Base64, subscription URL, or WG config:";
                    w.BtnAdd.Content = "Save Connection";
                    w.LblGeneralSettings.Text = "General Settings";
                    w.LblLanguage.Text = "Interface Language";
                    w.ChkAutoStartApp.Content = "Start gVPN with Windows";
                    w.ChkAutoStartConnect.Content = "Automatically connect selected server";
                    w.ChkStartMinimized.Content = "Start minimized to system tray";
                    w.LblRoutingTitle.Text = "Traffic Routing Mode";
                    w.RbTunnelAll.Content = "All PC traffic (Global VPN)";
                    w.RbTunnelApps.Content = "Only selected applications";
                    w.LblSelectedApps.Text = "Selected applications (.exe):";
                    w.BtnAddApp.Content = "+ Add .exe";
                    w.BtnBackToServers.Content = "Back to Servers";
                    break;

                case "pl":
                    w.LblAvailableServers.Text = "Dostępne serwery";
                    w.BtnToggleAddPanel.Content = w.PnlAddServer.Visibility == Visibility.Visible ? "Ukryj" : "+ Dodaj";
                    w.LblAddHint.Text = "Wklej vless://, trojan://, ss://, Base64, subskrypcję lub plik WG:";
                    w.BtnAdd.Content = "Zapisz połączenie";
                    w.LblGeneralSettings.Text = "Ustawienia ogólne";
                    w.LblLanguage.Text = "Język interfejsu";
                    w.ChkAutoStartApp.Content = "Uruchom gVPN wraz z systemem Windows";
                    w.ChkAutoStartConnect.Content = "Automatycznie połącz wybrany serwer";
                    w.ChkStartMinimized.Content = "Uruchom zminimalizowany w zasobniku";
                    w.LblRoutingTitle.Text = "Tryb routingu ruchu";
                    w.RbTunnelAll.Content = "Cały ruch komputera (Globalny VPN)";
                    w.RbTunnelApps.Content = "Tylko wybrane aplikacje";
                    w.LblSelectedApps.Text = "Wybrane aplikacje (.exe):";
                    w.BtnAddApp.Content = "+ Dodaj .exe";
                    w.BtnBackToServers.Content = "Powrót do serwerów";
                    break;

                default: // ru
                    w.LblAvailableServers.Text = "Доступные серверы";
                    w.BtnToggleAddPanel.Content = w.PnlAddServer.Visibility == Visibility.Visible ? "Скрыть" : "+ Добавить";
                    w.LblAddHint.Text = "Вставьте vless://, trojan://, ss://, Base64, URL подписки или WG конфиг:";
                    w.BtnAdd.Content = "Сохранить подключение";
                    w.LblGeneralSettings.Text = "Основные настройки";
                    w.LblLanguage.Text = "Язык интерфейса";
                    w.ChkAutoStartApp.Content = "Запускать gVPN вместе с Windows";
                    w.ChkAutoStartConnect.Content = "Автоматически подключать выбранный сервер";
                    w.ChkStartMinimized.Content = "Запускать свёрнутым в трей";
                    w.LblRoutingTitle.Text = "Режим маршрутизации трафика";
                    w.RbTunnelAll.Content = "Весь трафик ПК (Глобальный VPN)";
                    w.RbTunnelApps.Content = "Только выбранные приложения";
                    w.LblSelectedApps.Text = "Выбранные приложения (.exe):";
                    w.BtnAddApp.Content = "+ Добавить .exe";
                    w.BtnBackToServers.Content = "Назад к серверам";
                    break;
            }

            w.BtnPingAll.Content = s.PingAll;
            w.BtnSettings.ToolTip = s.TooltipSettings;
            w.BtnMinimize.ToolTip = s.TooltipMinimize;
            w.BtnClose.ToolTip = s.TooltipClose;
            w.Resources["LocLaunchApp"] = s.LaunchApp;
            w.Resources["LocDeleteTooltip"] = s.TooltipDelete;
            w.Resources["LocRenameTooltip"] = s.TooltipRename;

            w.BtnConnect.Content = isConnected ? s.Disconnect : s.Connect;
            w.TxtStatus.Text = isConnected ? string.Format(s.StatusConnectedFormat, profileName) : s.StatusDisconnected;
            if (w.PnlSettingsView.Visibility == Visibility.Visible)
                w.TxtHeaderTitle.Text = s.SettingsTitle;

            w.UpdateTrayLocalization(s, isConnected, profileName);
        }
    }
}